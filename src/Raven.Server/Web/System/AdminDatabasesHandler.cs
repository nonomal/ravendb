﻿// -----------------------------------------------------------------------
//  <copyright file="AdminDatabasesHandler.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Database;
using Raven.Client.Server;
using Raven.Client.Server.Commands;
using Raven.Client.Server.ETL;
using Raven.Client.Server.Operations;
using Raven.Server.Documents;
using Raven.Server.Json;
using Raven.Server.Routing;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Raven.Client.Server.PeriodicBackup;
using Raven.Server.Documents.Patch;

namespace Raven.Server.Web.System
{
    public class AdminDatabasesHandler : RequestHandler
    {
        [RavenAction("/admin/databases/is-loaded", "GET", "/admin/databases/is-loaded?name={databaseName:string}")]
        public Task IsDatabaseLoaded()
        {
            var name = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");

            HttpContext.Response.StatusCode = (int)HttpStatusCode.OK;
            var isLoaded = ServerStore.DatabasesLandlord.IsDatabaseLoaded(name);
            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    context.Write(writer, new DynamicJsonValue
                    {
                        [nameof(IsDatabaseLoadedCommand.CommandResult.DatabaseName)] = name,
                        [nameof(IsDatabaseLoadedCommand.CommandResult.IsLoaded)] = isLoaded
                    });
                    writer.Flush();
                }
            }
            return Task.CompletedTask;
        }

        [RavenAction("/admin/databases", "GET", "/admin/databases?name={databaseName:string}")]
        public Task Get()
        {
            var name = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");

            TransactionOperationContext context;
            using (ServerStore.ContextPool.AllocateOperationContext(out context))
            {
                var dbId = Constants.Documents.Prefix + name;
                long etag;
                using (context.OpenReadTransaction())
                using (var dbDoc = ServerStore.Cluster.Read(context, dbId, out etag))
                {
                    if (dbDoc == null)
                    {
                        HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        using (var writer = new BlittableJsonTextWriter(context, HttpContext.Response.Body))
                        {
                            context.Write(writer,
                                new DynamicJsonValue
                                {
                                    ["Type"] = "Error",
                                    ["Message"] = "Database " + name + " wasn't found"
                                });
                        }
                        return Task.CompletedTask;
                    }

                    using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                    {
                        writer.WriteDocument(context, new Document
                        {
                            Etag = etag,
                            Data = dbDoc,
                        });
                    }
                }
            }

            return Task.CompletedTask;
        }

        // add database to already existing database group
        [RavenAction("/admin/databases/add-node", "POST", "/admin/databases/add-node?name={databaseName:string}&node={nodeName:string|optional}")]
        public async Task AddDatabaseNode()
        {
            var name = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");
            var node = GetStringQueryString("node", false);

            string errorMessage;
            if (ResourceNameValidator.IsValidResourceName(name, ServerStore.Configuration.Core.DataDirectory.FullPath, out errorMessage) == false)
                throw new BadRequestException(errorMessage);

            ServerStore.EnsureNotPassive();
            TransactionOperationContext context;
            using (ServerStore.ContextPool.AllocateOperationContext(out context))
            using (context.OpenReadTransaction())
            {
                long etag;
                var databaseRecord = ServerStore.Cluster.ReadDatabase(context, name, out etag);
                var clusterTopology = ServerStore.GetClusterTopology(context);

                //The case where an explicit node was requested 
                if (string.IsNullOrEmpty(node) == false)
                {
                    if (databaseRecord.Topology.RelevantFor(node))
                        throw new InvalidOperationException($"Can't add node {node} to {name} topology because it is already part of it");

                    var url = clusterTopology.GetUrlFromTag(node);
                    if (url == null)
                        throw new InvalidOperationException($"Can't add node {node} to {name} topology because node {node} is not part of the cluster");

                    if (databaseRecord.Encrypted && NotUsingHttps(url))
                        throw new InvalidOperationException($"Can't add node {node} to database {name} topology because database {name} is encrypted but node {node} doesn't have an SSL certificate.");

                    databaseRecord.Topology.Promotables.Add(new DatabaseTopologyNode
                    {
                        Database = name,
                        NodeTag = node,
                        Url = url
                    });
                }

                //The case were we don't care where the database will be added to
                else
                {
                    var allNodes = clusterTopology.Members.Keys
                        .Concat(clusterTopology.Promotables.Keys)
                        .Concat(clusterTopology.Watchers.Keys)
                        .ToList();

                    allNodes.RemoveAll(n => databaseRecord.Topology.AllNodes.Contains(n) || (databaseRecord.Encrypted && NotUsingHttps(clusterTopology.GetUrlFromTag(n))));

                    if (databaseRecord.Encrypted && allNodes.Count == 0)
                        throw new InvalidOperationException($"Database {name} is encrypted and requires a node which supports SSL. There is no such node available in the cluster.");

                    if (allNodes.Count == 0)
                        throw new InvalidOperationException($"Database {name} already exists on all the nodes of the cluster");

                    var rand = new Random().Next();
                    var newNode = allNodes[rand % allNodes.Count];

                    databaseRecord.Topology.Promotables.Add(new DatabaseTopologyNode
                    {
                        Database = name,
                        NodeTag = newNode,
                        Url = clusterTopology.GetUrlFromTag(newNode)
                    });
                }

                var (newEtag, _) = await ServerStore.WriteDatabaseRecordAsync(name, databaseRecord, etag);
                await ServerStore.Cluster.WaitForIndexNotification(newEtag);

                HttpContext.Response.StatusCode = (int)HttpStatusCode.Created;

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    context.Write(writer, new DynamicJsonValue
                    {
                        [nameof(DatabasePutResult.ETag)] = newEtag,
                        [nameof(DatabasePutResult.Key)] = name,
                        [nameof(DatabasePutResult.Topology)] = databaseRecord.Topology.ToJson()
                    });
                    writer.Flush();
                }
            }
        }

        public bool NotUsingHttps(string url)
        {
            return url.StartsWith("https:", StringComparison.OrdinalIgnoreCase) == false;

        }

        [RavenAction("/admin/databases", "PUT", "/admin/databases/{databaseName:string}")]
        public async Task Put()
        {
            var name = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");
            var nodesAddedTo = new List<string>();

            if (ResourceNameValidator.IsValidResourceName(name, ServerStore.Configuration.Core.DataDirectory.FullPath, out string errorMessage) == false)
                throw new BadRequestException(errorMessage);

            ServerStore.EnsureNotPassive();
            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                context.OpenReadTransaction();

                var etag = GetLongFromHeaders("ETag");

                var json = context.ReadForDisk(RequestBodyStream(), name);
                var databaseRecord = JsonDeserializationCluster.DatabaseRecord(json);

                try
                {
                    DatabaseHelper.Validate(name, databaseRecord);
                }
                catch (Exception e)
                {
                    throw new BadRequestException("Database document validation failed.", e);
                }

                DatabaseTopology topology;
                if (databaseRecord.Topology?.Members?.Count > 0)
                {
                    topology = databaseRecord.Topology;
                    ValidateClusterMembers(context, topology, databaseRecord);
                }
                else
                {
                    var factor = Math.Max(1, GetIntValueQueryString("replication-factor", required: false) ?? 0);
                    databaseRecord.Topology = topology = AssignNodesToDatabase(context, factor, name, databaseRecord, out nodesAddedTo);
                }

                var (newEtag, _) = await ServerStore.WriteDatabaseRecordAsync(name, databaseRecord, etag);
                await ServerStore.Cluster.WaitForIndexNotification(newEtag);

                HttpContext.Response.StatusCode = (int)HttpStatusCode.Created;

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    context.Write(writer, new DynamicJsonValue
                    {
                        [nameof(DatabasePutResult.ETag)] = newEtag,
                        [nameof(DatabasePutResult.Key)] = name,
                        [nameof(DatabasePutResult.Topology)] = topology.ToJson(),
                        [nameof(DatabasePutResult.NodesAddedTo)] = nodesAddedTo
                    });
                    writer.Flush();
                }
            }
        }

        private DatabaseTopology AssignNodesToDatabase(TransactionOperationContext context, int factor, string name, DatabaseRecord databaseRecord, out List<string> nodesAddedTo)
        {
            var topology = new DatabaseTopology();

            var clusterTopology = ServerStore.GetClusterTopology(context);

            var allNodes = clusterTopology.Members.Keys
                .Concat(clusterTopology.Promotables.Keys)
                .Concat(clusterTopology.Watchers.Keys)
                .ToList();

            if (databaseRecord.Encrypted)
            {
                allNodes.RemoveAll(n => NotUsingHttps(clusterTopology.GetUrlFromTag(n)));
                if (allNodes.Count == 0)
                    throw new InvalidOperationException($"Database {name} is encrypted and requires a node which supports SSL. There is no such node available in the cluster.");
            }

            var offset = new Random().Next();
            nodesAddedTo = new List<string>();

            for (int i = 0; i < Math.Min(allNodes.Count, factor); i++)
            {
                var selectedNode = allNodes[(i + offset) % allNodes.Count];
                var url = clusterTopology.GetUrlFromTag(selectedNode);
                topology.Members.Add(new DatabaseTopologyNode
                {
                    Database = name,
                    NodeTag = selectedNode,
                    Url = url,
                });
                nodesAddedTo.Add(url);
            }

            return topology;
        }

        private void ValidateClusterMembers(TransactionOperationContext context, DatabaseTopology topology, DatabaseRecord databaseRecord)
        {
            var clusterTopology = ServerStore.GetClusterTopology(context);

            foreach (var node in topology.AllReplicationNodes())
            {
                var result = clusterTopology.TryGetNodeTagByUrl(node.Url);
                if (result.hasUrl == false || result.nodeTag != node.NodeTag)
                    throw new InvalidOperationException($"The Url {node.Url} for node {node.NodeTag} is not a part of the cluster, the incoming topology is wrong!");

                if (databaseRecord.Encrypted && NotUsingHttps(node.Url))
                    throw new InvalidOperationException($"{databaseRecord.DatabaseName} is encrypted but node {node.NodeTag} with url {node.Url} doesn't use HTTPS. This is not allowed.");
            }
        }

        [RavenAction("/admin/expiration/config", "POST", "/admin/config-expiration?name={databaseName:string}")]
        public async Task ConfigExpiration()
        {
            await DatabaseConfigurations(ServerStore.ModifyDatabaseExpiration, "read-expiration-config");
        }

        [RavenAction("/admin/versioning/config", "POST", "/admin/config-versioning?name={databaseName:string}")]
        public async Task ConfigVersioning()
        {
            await DatabaseConfigurations(ServerStore.ModifyDatabaseVersioning, "read-versioning-config");
        }

        [RavenAction("/admin/periodic-backup/update", "POST", "/admin/config-periodic-backup?name={databaseName:string}")]
        public async Task UpdatePeriodicBackup()
        {
            await DatabaseConfigurations(ServerStore.ModifyPeriodicBackup,
                "update-periodic-backup",
                fillJson: (json, readerObject, index) =>
                {
                    readerObject.TryGet("TaskId", out long taskId);
                    if (taskId == 0)
                        taskId = index;
                    json[nameof(PeriodicBackupConfiguration.TaskId)] = taskId;
                });
        }

        [RavenAction("/admin/periodic-backup/status", "GET", "/admin/delete-periodic-status?name={databaseName:string}")]
        public Task GetPeriodicBackupStatus()
        {
            var taskId = GetLongQueryString("taskId", required: true);
            Debug.Assert(taskId != 0);

            var name = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");
            if (ResourceNameValidator.IsValidResourceName(name, ServerStore.Configuration.Core.DataDirectory.FullPath, out string errorMessage) == false)
                throw new BadRequestException(errorMessage);

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            using (context.OpenReadTransaction())
            using (var statusBlittable =
                ServerStore.Cluster.Read(context, PeriodicBackupStatus.GenerateItemName(name, taskId.Value)))
            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                writer.WriteStartObject();
                writer.WritePropertyName(nameof(GetPeriodicBackupStatusOperationResult.Status));
                writer.WriteObject(statusBlittable);
                writer.WriteEndObject();
                writer.Flush();
            }

            return Task.CompletedTask;
        }

        private async Task DatabaseConfigurations(Func<TransactionOperationContext, string,
            BlittableJsonReaderObject, Task<(long, object)>> setupConfigurationFunc,
            string debug,
            Action<DynamicJsonValue, BlittableJsonReaderObject, long> fillJson = null)
        {
            var name = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");
            string errorMessage;
            if (ResourceNameValidator.IsValidResourceName(name, ServerStore.Configuration.Core.DataDirectory.FullPath, out errorMessage) == false)
                throw new BadRequestException(errorMessage);

            ServerStore.EnsureNotPassive();
            TransactionOperationContext context;
            using (ServerStore.ContextPool.AllocateOperationContext(out context))
            {
                var configurationJson = await context.ReadForMemoryAsync(RequestBodyStream(), debug);
                var (etag, _) = await setupConfigurationFunc(context, name, configurationJson);
                DatabaseRecord dbRecord;
                using (context.OpenReadTransaction())
                {
                    //TODO: maybe have a timeout here for long loading operations
                    dbRecord = ServerStore.Cluster.ReadDatabase(context, name);
                }
                if (dbRecord.Topology.RelevantFor(ServerStore.NodeTag))
                {
                    var db = await ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(name);
                    await db.RachisLogIndexNotifications.WaitForIndexNotification(etag);
                }
                else
                {
                    await ServerStore.Cluster.WaitForIndexNotification(etag);
                }
                HttpContext.Response.StatusCode = (int)HttpStatusCode.OK;

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    var json = new DynamicJsonValue
                    {
                        ["ETag"] = etag
                    };
                    fillJson?.Invoke(json, configurationJson, etag);
                    context.Write(writer, json);
                    writer.Flush();
                }
            }
        }

        [RavenAction("/admin/modify-custom-functions", "POST", "/admin/modify-custom-functions?name={databaseName:string}")]
        public async Task ModifyCustomFunctions()
        {
            var name = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");
            if (ResourceNameValidator.IsValidResourceName(name, ServerStore.Configuration.Core.DataDirectory.FullPath, out string errorMessage) == false)
                throw new BadRequestException(errorMessage);

            ServerStore.EnsureNotPassive();

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                var updateJson = await context.ReadForMemoryAsync(RequestBodyStream(), "read-modify-custom-functions");
                string functions;
                if (updateJson.TryGet(nameof(CustomFunctions.Functions), out functions) == false)
                {
                    throw new InvalidDataException("Functions property was not found.");
                }

                var (etag, _) = await ServerStore.ModifyCustomFunctions(name, functions);
                await ServerStore.Cluster.WaitForIndexNotification(etag);

                HttpContext.Response.StatusCode = (int)HttpStatusCode.Created;

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    context.Write(writer, new DynamicJsonValue
                    {
                        [nameof(DatabasePutResult.ETag)] = etag,
                    });
                    writer.Flush();
                }
            }
        }

        [RavenAction("/admin/update-resolver", "POST", "/admin/update-resolver?name={databaseName:string}")]
        public async Task ChangeConflictResolver()
        {
            var name = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");
            if (ResourceNameValidator.IsValidResourceName(name, ServerStore.Configuration.Core.DataDirectory.FullPath, out string errorMessage) == false)
                throw new BadRequestException(errorMessage);

            ServerStore.EnsureNotPassive();
            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                var json = await context.ReadForMemoryAsync(RequestBodyStream(), "read-conflict-resolver");
                var conflictResolver = (ConflictSolver)EntityToBlittable.ConvertToEntity(typeof(ConflictSolver), "convert-conflict-resolver", json, DocumentConventions.Default);

                using (context.OpenReadTransaction())
                {
                    var databaseRecord = ServerStore.Cluster.ReadDatabase(context, name, out _);

                    var (etag, _) = await ServerStore.ModifyConflictSolverAsync(name, conflictResolver);
                    await ServerStore.Cluster.WaitForIndexNotification(etag);

                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Created;

                    using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                    {
                        context.Write(writer, new DynamicJsonValue
                        {
                            ["ETag"] = etag,
                            ["Key"] = name,
                            [nameof(DatabaseRecord.ConflictSolverConfig)] = databaseRecord.ConflictSolverConfig.ToJson()
                        });
                        writer.Flush();
                    }
                }
            }
        }

        [RavenAction("/admin/databases", "DELETE", "/admin/databases?name={databaseName:string|multiple}&hard-delete={isHardDelete:bool|optional(false)}&from-node={nodeToDelete:string|optional(null)}")]
        public async Task Delete()
        {
            var names = GetStringValuesQueryString("name");
            var fromNode = GetStringValuesQueryString("from-node", required: false).FirstOrDefault();
            var isHardDelete = GetBoolValueQueryString("hard-delete", required: false) ?? false;
            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                if (string.IsNullOrEmpty(fromNode) == false)
                {
                    using (context.OpenReadTransaction())
                    {
                        foreach (var databaseName in names)
                        {
                            if (ServerStore.Cluster.ReadDatabase(context, databaseName)?.Topology.RelevantFor(fromNode) == false)
                            {
                                throw new InvalidOperationException($"Database={databaseName} doesn't reside in node={fromNode} so it can't be deleted from it");
                            }
                        }
                    }
                }

                long etag = -1;
                foreach (var name in names)
                {
                    var (newEtag, _) = await ServerStore.DeleteDatabaseAsync(name, isHardDelete, fromNode);
                    etag = newEtag;
                }
                await ServerStore.Cluster.WaitForIndexNotification(etag);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.OK;

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    context.Write(writer, new DynamicJsonValue
                    {
                        ["ETag"] = etag
                    });
                    writer.Flush();
                }
            }
        }

        [RavenAction("/admin/databases/disable", "POST", "/admin/databases/disable?name={resourceName:string|multiple}")]
        public async Task DisableDatabases()
        {
            await ToggleDisableDatabases(disableRequested: true);
        }

        [RavenAction("/admin/databases/enable", "POST", "/admin/databases/enable?name={resourceName:string|multiple}")]
        public async Task EnableDatabases()
        {
            await ToggleDisableDatabases(disableRequested: false);
        }

        private async Task ToggleDisableDatabases(bool disableRequested)
        {
            var names = GetStringValuesQueryString("name");

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("Status");

                writer.WriteStartArray();
                var first = true;
                foreach (var name in names)
                {
                    if (first == false)
                        writer.WriteComma();
                    first = false;

                    DatabaseRecord databaseRecord;
                    using (context.OpenReadTransaction())
                        databaseRecord = ServerStore.Cluster.ReadDatabase(context, name);

                    if (databaseRecord == null)
                    {
                        context.Write(writer, new DynamicJsonValue
                        {
                            ["Name"] = name,
                            ["Success"] = false,
                            ["Reason"] = "database not found",
                        });
                        continue;
                    }

                    if (databaseRecord.Disabled == disableRequested)
                    {
                        var state = disableRequested ? "disabled" : "enabled";
                        context.Write(writer, new DynamicJsonValue
                        {
                            ["Name"] = name,
                            ["Success"] = true, //even if we have nothing to do, no reason to return failure status
                            ["Disabled"] = disableRequested,
                            ["Reason"] = $"Database already {state}",
                        });
                        continue;
                    }

                    databaseRecord.Disabled = disableRequested;

                    var (etag, _) = await ServerStore.WriteDatabaseRecordAsync(name, databaseRecord, null);
                    await ServerStore.Cluster.WaitForIndexNotification(etag);

                    context.Write(writer, new DynamicJsonValue
                    {
                        ["Name"] = name,
                        ["Success"] = true,
                        ["Disabled"] = disableRequested,
                        ["Reason"] = $"Database state={databaseRecord.Disabled} was propagated on the cluster"
                    });
                }

                writer.WriteEndArray();

                writer.WriteEndObject();
            }
        }

        [RavenAction("/admin/etl/add", "PUT", "/admin/etl/add?name={databaseName:string}&type={[sql|raven]:string}")]
        public async Task AddEtl()
        {
            var type = GetQueryStringValueAndAssertIfSingleAndNotEmpty("type");

            if (Enum.TryParse<EtlType>(type, true, out var etlType) == false)
                throw new ArgumentException($"Unknown ETL type: {type}", "type");

            await DatabaseConfigurations((_, databaseName, etlConfiguration) => ServerStore.AddEtl(_, databaseName, etlConfiguration, etlType), "etl-add");
        }

        [RavenAction("/admin/etl/update", "POST", "/admin/etl/update?id={id:ulong}&name={databaseName:string}&type={[sql|raven]:string}")]
        public async Task UpdateEtl()
        {
            var type = GetQueryStringValueAndAssertIfSingleAndNotEmpty("type");
            var id = GetLongQueryString("id");

            if (Enum.TryParse<EtlType>(type, true, out var etlType) == false)
                throw new ArgumentException($"Unknown ETL type: {type}", "type");

            await DatabaseConfigurations((_, databaseName, etlConfiguration) => ServerStore.UpdateEtl(_, databaseName, id, etlConfiguration, etlType), "etl-update");
        }

        [RavenAction("/admin/console", "POST", "/admin/console?database={databaseName:string}&server-script={isServerScript:bool|optional(false)}")]
        public async Task AdminConsole()
        {
            var name = GetStringQueryString("database", false);
            var isServerScript = GetBoolValueQueryString("server-script", false) ?? false;
            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                var content = await context.ReadForMemoryAsync(RequestBodyStream(), "read-admin-script");
                if (content.TryGet(nameof(AdminJsScript), out BlittableJsonReaderObject adminJsBlittable) == false)
                {
                    throw new InvalidDataException("AdminJsScript was not found.");
                }

                var adminJsScript = JsonDeserializationCluster.AdminJsScript(adminJsBlittable);
                DynamicJsonValue result;

                if (isServerScript)
                {
                    var console = new AdminJsConsole(Server);
                    result = console.ApplyServerScript(adminJsScript);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.OK;
                }

                else if (string.IsNullOrWhiteSpace(name) == false)
                {
                    //database script
                    var database = await ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(name);
                    if (database == null)
                    {
                        DatabaseDoesNotExistException.Throw(name);
                    }

                    var console = new AdminJsConsole(database);
                    result = console.ApplyScript(adminJsScript);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.OK;
                }

                else
                {
                    throw new InvalidOperationException("'database' query string parmater not found, and 'server-script' query string is not found. Don't know what to apply this script on");
                }

                if (result != null)
                {
                    using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                    {
                        context.Write(writer, result);
                        writer.Flush();
                    }
                }
            }
        }
    }
}