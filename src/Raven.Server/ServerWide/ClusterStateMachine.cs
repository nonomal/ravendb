﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions.Cluster;
using Raven.Client.Exceptions.Database;
using Raven.Client.Exceptions.Security;
using Raven.Client.Http.OAuth;
using Raven.Client.Server;
using Raven.Client.Server.Operations.ApiKeys;
using Raven.Client.Server.Tcp;
using Raven.Server.Json;
using Raven.Server.Rachis;
using Raven.Server.ServerWide.Commands;
using Raven.Server.ServerWide.Commands.ETL;
using Raven.Server.ServerWide.Commands.Indexes;
using Raven.Server.ServerWide.Commands.PeriodicBackup;
using Raven.Server.ServerWide.Commands.Subscriptions;
using Raven.Server.ServerWide.Commands.Transformers;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Sparrow;
using Sparrow.Binary;
using Sparrow.Collections;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Sparrow.Utils;
using Voron;
using Voron.Data;
using Voron.Data.Tables;
using Voron.Exceptions;
using Voron.Impl;

namespace Raven.Server.ServerWide
{
    public class ClusterStateMachine : RachisStateMachine
    {
        private static readonly TableSchema ItemsSchema;
        private static readonly Slice EtagIndexName;
        private static readonly Slice Items;

        static ClusterStateMachine()
        {
            Slice.From(StorageEnvironment.LabelsContext, "Items", out Items);
            Slice.From(StorageEnvironment.LabelsContext, "EtagIndexName", out EtagIndexName);

            ItemsSchema = new TableSchema();

            // We use the follow format for the items data
            // { lowered key, key, data, etag }
            ItemsSchema.DefineKey(new TableSchema.SchemaIndexDef
            {
                StartIndex = 0,
                Count = 1
            });

            ItemsSchema.DefineFixedSizeIndex(new TableSchema.FixedSizeSchemaIndexDef
            {
                Name = EtagIndexName,
                IsGlobal = true,
                StartIndex = 3
            });
        }

        public event EventHandler<(string dbName, long index, string type)> DatabaseChanged;

        public event EventHandler<(string dbName, long index, string type)> DatabaseValueChanged;

        protected override void Apply(TransactionOperationContext context, BlittableJsonReaderObject cmd, long index, Leader leader)
        {
            if (cmd.TryGet("Type", out string type) == false)
                return;
            string errorMessage;
            switch (type)
            {
                //The reason we have a separate case for removing node from database is because we must 
                //actually delete the database before we notify about changes to the record otherwise we 
                //don't know that it was us who needed to delete the database.
                case nameof(RemoveNodeFromDatabaseCommand):
                    RemoveNodeFromDatabase(context, cmd, index, leader);
                    break;

                case nameof(DeleteValueCommand):
                    DeleteValue(context, cmd, index, leader);
                    break;
                case nameof(IncrementClusterIdentityCommand):
                    if (!ValidatePropertyExistance(cmd,
                        nameof(IncrementClusterIdentityCommand),
                        nameof(IncrementClusterIdentityCommand.Prefix), 
                        out errorMessage))
                    {
                        NotifyLeaderAboutError(index, leader,
                            new InvalidDataException(errorMessage));
                        return;
                    }


                    var updatedDatabaseRecord = UpdateDatabase(context, type, cmd, index, leader);

                    cmd.TryGet(nameof(IncrementClusterIdentityCommand.Prefix), out string prefix);
                    Debug.Assert(prefix != null,"since we verified that the property exist, it must not be null");

                    leader?.SetStateOf(index, updatedDatabaseRecord.Identities[prefix]);
                    break;
                case nameof(UpdateClusterIdentityCommand):
                    if (!ValidatePropertyExistance(cmd,
                        nameof(UpdateClusterIdentityCommand),
                        nameof(UpdateClusterIdentityCommand.Identities),
                        out errorMessage))
                    {
                        NotifyLeaderAboutError(index, leader,
                            new InvalidDataException(errorMessage));
                        return;
                    }
                    UpdateDatabase(context, type, cmd, index, leader);
                    break;
                case nameof(PutIndexCommand):
                case nameof(PutAutoIndexCommand):
                case nameof(DeleteIndexCommand):
                case nameof(SetIndexLockCommand):
                case nameof(SetIndexPriorityCommand):
                case nameof(PutTransformerCommand):
                case nameof(SetTransformerLockCommand):
                case nameof(DeleteTransformerCommand):
                case nameof(RenameTransformerCommand):
                case nameof(EditVersioningCommand):
                case nameof(UpdatePeriodicBackupCommand):
                case nameof(EditExpirationCommand):
                case nameof(ModifyConflictSolverCommand):
                case nameof(UpdateTopologyCommand):
                case nameof(DeleteDatabaseCommand):
                case nameof(ModifyCustomFunctionsCommand):
                case nameof(UpdateExternalReplicationCommand):
                case nameof(ToggleTaskStateCommand):
                case nameof(AddRavenEtlCommand):
                case nameof(AddSqlEtlCommand):
                case nameof(UpdateRavenEtlCommand):
                case nameof(UpdateSqlEtlCommand):
                case nameof(DeleteOngoingTaskCommand):
                    UpdateDatabase(context, type, cmd, index, leader);
                    break;
                case nameof(UpdatePeriodicBackupStatusCommand):
                case nameof(AcknowledgeSubscriptionBatchCommand):
                case nameof(CreateSubscriptionCommand):
                case nameof(DeleteSubscriptionCommand):
                case nameof(UpdateEtlProcessStateCommand):
                    SetValueForTypedDatabaseCommand(context, type, cmd, index, leader);
                    break;
                case nameof(PutApiKeyCommand):
                    PutValue<ApiKeyDefinition>(context, cmd, index, leader);
                    break;
                case nameof(AddDatabaseCommand):
                    AddDatabase(context, cmd, index, leader);
                    break;
            }
        }

        private static bool ValidatePropertyExistance(BlittableJsonReaderObject cmd, string propertyTypeName, string propertyName, out string errorMessage)
        {
            errorMessage = null;
            if (cmd.TryGet(propertyName, out object _) == false)
            {
                errorMessage = $"Expected to find {propertyTypeName}.{propertyName} property in the Raft command but didn't find it...";               
                return false;
            }
            return true;
        }

        private unsafe void SetValueForTypedDatabaseCommand(TransactionOperationContext context, string type, BlittableJsonReaderObject cmd, long index, Leader leader)
        {
            UpdateValueForDatabaseCommand updateCommand = null;
            try
            {
                var items = context.Transaction.InnerTransaction.OpenTable(ItemsSchema, Items);

                updateCommand = (UpdateValueForDatabaseCommand)JsonDeserializationCluster.Commands[type](cmd);

                var record = ReadDatabase(context, updateCommand.DatabaseName);
                if (record == null)
                {
                    NotifyLeaderAboutError(index, leader, new CommandExecutionException($"Cannot set typed value of type {type} for database {updateCommand.DatabaseName}, because does not exist"));
                    return;
                }

                BlittableJsonReaderObject itemBlittable = null;

                var itemKey = updateCommand.GetItemId();
                using (Slice.From(context.Allocator, itemKey.ToLowerInvariant(), out Slice valueNameLowered))
                {
                    if (items.ReadByKey(valueNameLowered, out TableValueReader reader))
                    {
                        var ptr = reader.Read(2, out int size);
                        itemBlittable = new BlittableJsonReaderObject(ptr, size, context);
                    }

                    try
                    {
                        itemBlittable = updateCommand.GetUpdatedValue(index, record, context, itemBlittable, _parent.CurrentState == RachisConsensus.State.Passive);

                        // if returned null, means, there is nothing to update and we just wanted to delete the value
                        if (itemBlittable == null)
                        {
                            items.DeleteByKey(valueNameLowered);
                            return;
                        }

                        // here we get the item key again, in case it was changed (a new entity, etc)
                        itemKey = updateCommand.GetItemId();
                    }
                    catch (Exception e)
                    {
                        NotifyLeaderAboutError(index, leader,
                            new CommandExecutionException($"Cannot set typed value of type {type} for database {updateCommand.DatabaseName}, because does not exist", e));
                        return;
                    }
                }

                using (Slice.From(context.Allocator, itemKey, out Slice valueName))
                using (Slice.From(context.Allocator, itemKey.ToLowerInvariant(), out Slice valueNameLowered))
                {
                    UpdateValue(index, items, valueNameLowered, valueName, itemBlittable);
                }
            }
            finally
            {
                NotifyDatabaseValueChanged(context, updateCommand?.DatabaseName, index, type);
            }
        }

        private readonly RachisLogIndexNotifications _rachisLogIndexNotifications = new RachisLogIndexNotifications(CancellationToken.None);

        public async Task WaitForIndexNotification(long index)
        {
            await _rachisLogIndexNotifications.WaitForIndexNotification(index, _parent.OperationTimeout);
        }

        private unsafe void RemoveNodeFromDatabase(TransactionOperationContext context, BlittableJsonReaderObject cmd, long index, Leader leader)
        {
            var items = context.Transaction.InnerTransaction.OpenTable(ItemsSchema, Items);
            var remove = JsonDeserializationCluster.RemoveNodeFromDatabaseCommand(cmd);
            var databaseName = remove.DatabaseName;
            var databaseNameLowered = databaseName.ToLowerInvariant();
            using (Slice.From(context.Allocator, "db/" + databaseNameLowered, out Slice lowerKey))
            using (Slice.From(context.Allocator, "db/" + databaseName, out Slice key))
            {
                if (items.ReadByKey(lowerKey, out TableValueReader reader) == false)
                {
                    NotifyLeaderAboutError(index, leader, new InvalidOperationException($"The database {databaseName} does not exists"));
                    return;
                }
                var doc = new BlittableJsonReaderObject(reader.Read(2, out int size), size, context);

                var databaseRecord = JsonDeserializationCluster.DatabaseRecord(doc);

                if (doc.TryGet(nameof(DatabaseRecord.Topology), out BlittableJsonReaderObject topology) == false)
                {
                    items.DeleteByKey(lowerKey);
                    NotifyDatabaseChanged(context, databaseName, index, nameof(RemoveNodeFromDatabaseCommand));
                    return;
                }
                remove.UpdateDatabaseRecord(databaseRecord, index);

                if (databaseRecord.Topology.AllNodes.Any() == false)
                {
                    // delete database record
                    items.DeleteByKey(lowerKey);

                    // delete all values linked to database record - for subscription, etl etc.
                    CleanupDatabaseRelatedValues(context, items, databaseName);

                    items.DeleteByPrimaryKeyPrefix(lowerKey);
                    NotifyDatabaseChanged(context, databaseName, index, nameof(RemoveNodeFromDatabaseCommand));
                    return;
                }

                var updated = EntityToBlittable.ConvertEntityToBlittable(databaseRecord, DocumentConventions.Default, context);

                UpdateValue(index, items, lowerKey, key, updated);

                NotifyDatabaseChanged(context, databaseName, index, nameof(RemoveNodeFromDatabaseCommand));
            }
        }

        private static void CleanupDatabaseRelatedValues(TransactionOperationContext context, Table items, string dbNameLowered)
        {
            var dbValuesPrefix = Helpers.ClusterStateMachineValuesPrefix(dbNameLowered).ToLowerInvariant();
            using (Slice.From(context.Allocator, dbValuesPrefix, out Slice loweredKey))
            {
                items.DeleteByPrimaryKeyPrefix(loweredKey);
            }
        }

        private static unsafe void UpdateValue(long index, Table items, Slice lowerKey, Slice key, BlittableJsonReaderObject updated)
        {
            using (items.Allocate(out TableValueBuilder builder))
            {
                builder.Add(lowerKey);
                builder.Add(key);
                builder.Add(updated.BasePointer, updated.Size);
                builder.Add(Bits.SwapBytes(index));

                items.Set(builder);
            }
        }

        private unsafe void AddDatabase(TransactionOperationContext context, BlittableJsonReaderObject cmd, long index, Leader leader)
        {
            var addDatabaseCommand = JsonDeserializationCluster.AddDatabaseCommand(cmd);
            try
            {
                var items = context.Transaction.InnerTransaction.OpenTable(ItemsSchema, Items);
                using (Slice.From(context.Allocator, "db/" + addDatabaseCommand.Name, out Slice valueName))
                using (Slice.From(context.Allocator, "db/" + addDatabaseCommand.Name.ToLowerInvariant(), out Slice valueNameLowered))
                using (var databaseRecordAsJson = EntityToBlittable.ConvertEntityToBlittable(addDatabaseCommand.Record, DocumentConventions.Default, context))
                {
                    if (addDatabaseCommand.RaftCommandIndex != null)
                    {
                        if (items.ReadByKey(valueNameLowered, out TableValueReader reader) == false && addDatabaseCommand.RaftCommandIndex != 0)
                        {
                            NotifyLeaderAboutError(index, leader, new ConcurrencyException("Concurrency violation, the database " + addDatabaseCommand.Name + " does not exists, but had a non zero etag"));
                            return;
                        }

                        var actualEtag = Bits.SwapBytes(*(long*)reader.Read(3, out int size));
                        Debug.Assert(size == sizeof(long));

                        if (actualEtag != addDatabaseCommand.RaftCommandIndex.Value)
                        {
                            NotifyLeaderAboutError(index, leader,
                                new ConcurrencyException("Concurrency violation, the database " + addDatabaseCommand.Name + " has etag " + actualEtag + " but was expecting " + addDatabaseCommand.RaftCommandIndex));
                            return;
                        }
                    }

                    UpdateValue(index, items, valueNameLowered, valueName, databaseRecordAsJson);
                    SetDatabaseValues(addDatabaseCommand.DatabaseValues, context, index, items);
                }
            }
            finally
            {
                NotifyDatabaseChanged(context, addDatabaseCommand.Name, index, nameof(AddDatabaseCommand));
            }
        }

        private static void SetDatabaseValues(
            Dictionary<string, object> databaseValues, 
            TransactionOperationContext context, 
            long index, 
            Table items)
        {
            if (databaseValues == null)
                return;

            foreach (var keyValue in databaseValues)
            {
                using (Slice.From(context.Allocator, keyValue.Key, out Slice databaseValueName))
                using (Slice.From(context.Allocator, keyValue.Key.ToLowerInvariant(), out Slice databaseValueNameLowered))
                using (var value = EntityToBlittable.ConvertEntityToBlittable(keyValue.Value, DocumentConventions.Default, context))
                {
                    UpdateValue(index, items, databaseValueNameLowered, databaseValueName, value);
                }
            }
        }

        private void DeleteValue(TransactionOperationContext context, BlittableJsonReaderObject cmd, long index, Leader leader)
        {
            try
            {
                var items = context.Transaction.InnerTransaction.OpenTable(ItemsSchema, Items);
                var delCmd = JsonDeserializationCluster.DeleteValueCommand(cmd);
                if (delCmd.Name.StartsWith("db/"))
                {
                    NotifyLeaderAboutError(index, leader, new InvalidOperationException("Cannot set " + delCmd.Name + " using DeleteValueCommand, only via dedicated Database calls"));
                    return;
                }
                using (Slice.From(context.Allocator, delCmd.Name, out Slice str))
                {
                    items.DeleteByKey(str);
                }
            }
            finally
            {
                NotifyIndexProcessed(context, index);
            }
        }

        private void PutValue<T>(TransactionOperationContext context, BlittableJsonReaderObject cmd, long index, Leader leader)
        {
            try
            {
                var items = context.Transaction.InnerTransaction.OpenTable(ItemsSchema, Items);
                var putVal = (PutValueCommand<T>)CommandBase.CreateFrom(cmd);
                if (putVal.Name.StartsWith("db/"))
                {
                    NotifyLeaderAboutError(index, leader, new InvalidOperationException("Cannot set " + putVal.Name + " using PutValueCommand, only via dedicated Database calls"));
                    return;
                }

                using (Slice.From(context.Allocator, putVal.Name, out Slice valueName))
                using (Slice.From(context.Allocator, putVal.Name.ToLowerInvariant(), out Slice valueNameLowered))
                using (var rec = context.ReadObject(putVal.ValueToJson(), "inner-val"))
                {
                    UpdateValue(index, items, valueNameLowered, valueName, rec);
                }
            }
            finally
            {
                NotifyIndexProcessed(context, index);
            }
        }

        private void NotifyIndexProcessed(TransactionOperationContext context, long index)
        {
            context.Transaction.InnerTransaction.LowLevelTransaction.OnDispose += transaction =>
            {
                if (transaction is LowLevelTransaction llt && llt.Committed)
                    _rachisLogIndexNotifications.NotifyListenersAbout(index);
            };
        }

        private void NotifyDatabaseChanged(TransactionOperationContext context, string databaseName, long index, string type)
        {
            context.Transaction.InnerTransaction.LowLevelTransaction.OnDispose += transaction =>
            {
                if (transaction is LowLevelTransaction llt && llt.Committed)
                    TaskExecutor.Execute(_ =>
                    {
                        try
                        {
                            DatabaseChanged?.Invoke(this, (databaseName, index, type));
                        }
                        finally
                        {
                            _rachisLogIndexNotifications.NotifyListenersAbout(index);
                        }
                    }, null);
            };
        }

        private void NotifyDatabaseValueChanged(TransactionOperationContext context, string databaseName, long index, string type)
        {
            context.Transaction.InnerTransaction.LowLevelTransaction.OnDispose += transaction =>
            {
                if (transaction is LowLevelTransaction llt && llt.Committed)
                    TaskExecutor.Execute(_ =>
                    {
                        try
                        {
                            DatabaseValueChanged?.Invoke(this, (databaseName, index, type));
                        }
                        finally
                        {
                            _rachisLogIndexNotifications.NotifyListenersAbout(index);
                        }
                    }, null);
            };
        }

        private static readonly StringSegment DatabaseName = new StringSegment("DatabaseName");

        private DatabaseRecord UpdateDatabase(TransactionOperationContext context, string type, BlittableJsonReaderObject cmd, long index, Leader leader)
        {
            if (cmd.TryGet(DatabaseName, out string databaseName) == false)
                throw new ArgumentException("Update database command must contain a DatabaseName property");

            DatabaseRecord databaseRecord;
            try
            {
                var items = context.Transaction.InnerTransaction.OpenTable(ItemsSchema, Items);
                var dbKey = "db/" + databaseName;

                using (Slice.From(context.Allocator, dbKey, out Slice valueName))
                using (Slice.From(context.Allocator, dbKey.ToLowerInvariant(), out Slice valueNameLowered))
                {
                    var databaseRecordJson = ReadInternal(context, out long etag, valueNameLowered);

                    var updateCommand = (UpdateDatabaseCommand)JsonDeserializationCluster.Commands[type](cmd);

                    if (databaseRecordJson == null)
                    {
                        if (updateCommand.ErrorOnDatabaseDoesNotExists)
                            NotifyLeaderAboutError(index, leader, DatabaseDoesNotExistException.CreateWithMessage(databaseName, $"Could not execute update command of type '{type}'."));
                        return null;
                    }

                    databaseRecord = JsonDeserializationCluster.DatabaseRecord(databaseRecordJson);

                    if (updateCommand.RaftCommandIndex != null && etag != updateCommand.RaftCommandIndex.Value)
                    {
                        NotifyLeaderAboutError(index, leader,
                            new ConcurrencyException($"Concurrency violation at executing {type} command, the database {databaseRecord.DatabaseName} has etag {etag} but was expecting {updateCommand.RaftCommandIndex}"));
                        return null;
                    }

                    try
                    {
                        var relatedRecordIdToDelete = updateCommand.UpdateDatabaseRecord(databaseRecord, index);
                        if (relatedRecordIdToDelete != null)
                        {
                            var itemKey = relatedRecordIdToDelete;
                            using (Slice.From(context.Allocator, itemKey, out Slice _))
                            using (Slice.From(context.Allocator, itemKey.ToLowerInvariant(), out Slice valueNameToDeleteLowered))
                            {
                                items.DeleteByKey(valueNameToDeleteLowered);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        NotifyLeaderAboutError(index, leader, new CommandExecutionException($"Cannot execute command of type {type} for database {databaseName}", e));
                        return null;
                    }

                    var updatedDatabaseBlittable = EntityToBlittable.ConvertEntityToBlittable(databaseRecord, DocumentConventions.Default, context);
                    UpdateValue(index, items, valueNameLowered, valueName, updatedDatabaseBlittable);
                }
            }
            finally
            {
                NotifyDatabaseChanged(context, databaseName, index, type);
            }

            return databaseRecord;
        }

        private static void NotifyLeaderAboutError(long index, Leader leader, Exception e)
        {
            // ReSharper disable once UseNullPropagation
            if (leader == null)
                return;

            leader.SetStateOf(index, tcs =>
            {
                tcs.TrySetException(e);
            });
        }

        public override bool ShouldSnapshot(Slice slice, RootObjectType type)
        {
            return slice.Content.Match(Items.Content);
        }

        public override void Initialize(RachisConsensus parent, TransactionOperationContext context)
        {
            base.Initialize(parent, context);
            ItemsSchema.Create(context.Transaction.InnerTransaction, Items, 32);
        }

        public IEnumerable<Tuple<string, BlittableJsonReaderObject>> ItemsStartingWith(TransactionOperationContext context, string prefix, int start, int take)
        {
            var items = context.Transaction.InnerTransaction.OpenTable(ItemsSchema, Items);

            var dbKey = prefix.ToLowerInvariant();
            using (Slice.From(context.Allocator, dbKey, out Slice loweredPrefix))
            {
                foreach (var result in items.SeekByPrimaryKeyPrefix(loweredPrefix, Slices.Empty, start))
                {
                    if (take-- <= 0)
                        yield break;

                    yield return GetCurrentItem(context, result.Value);
                }
            }
        }

        public IEnumerable<string> GetDatabaseNames(TransactionOperationContext context, int start = 0, int take = int.MaxValue)
        {
            var items = context.Transaction.InnerTransaction.OpenTable(ItemsSchema, Items);

            var dbKey = "db/";
            using (Slice.From(context.Allocator, dbKey, out Slice loweredPrefix))
            {
                foreach (var result in items.SeekByPrimaryKeyPrefix(loweredPrefix, Slices.Empty, 0))
                {
                    if (take-- <= 0)
                        yield break;

                    yield return GetCurrentItemKey(context, result.Value).Substring(3);
                }
            }
        }

        private static unsafe string GetCurrentItemKey(TransactionOperationContext context, Table.TableValueHolder result)
        {
            return Encoding.UTF8.GetString(result.Reader.Read(1, out int size), size);
        }

        private static unsafe Tuple<string, BlittableJsonReaderObject> GetCurrentItem(TransactionOperationContext context, Table.TableValueHolder result)
        {
            var ptr = result.Reader.Read(2, out int size);
            var doc = new BlittableJsonReaderObject(ptr, size, context);

            var key = Encoding.UTF8.GetString(result.Reader.Read(1, out size), size);

            return Tuple.Create(key, doc);
        }

        public DatabaseRecord ReadDatabase(TransactionOperationContext context, string name)
        {
            return ReadDatabase(context, name, out long _);
        }

        public DatabaseRecord ReadDatabase<T>(TransactionOperationContext<T> context, string name, out long etag)
            where T : RavenTransaction
        {
            var doc = Read(context, "db/" + name.ToLowerInvariant(), out etag);
            if (doc == null)
                return null;
            return JsonDeserializationCluster.DatabaseRecord(doc);
        }

        public BlittableJsonReaderObject Read<T>(TransactionOperationContext<T> context, string name)
            where T : RavenTransaction
        {
            return Read(context, name, out long _);
        }

        public BlittableJsonReaderObject Read<T>(TransactionOperationContext<T> context, string name, out long etag)
            where T : RavenTransaction
        {

            var dbKey = name.ToLowerInvariant();
            using (Slice.From(context.Allocator, dbKey, out Slice key))
            {
                return ReadInternal(context, out etag, key);
            }
        }

        private static unsafe BlittableJsonReaderObject ReadInternal<T>(TransactionOperationContext<T> context, out long etag, Slice key)
            where T : RavenTransaction
        {
            var items = context.Transaction.InnerTransaction.OpenTable(ItemsSchema, Items);
            if (items.ReadByKey(key, out TableValueReader reader) == false)
            {
                etag = 0;
                return null;
            }

            var ptr = reader.Read(2, out int size);
            var doc = new BlittableJsonReaderObject(ptr, size, context);

            etag = Bits.SwapBytes(*(long*)reader.Read(3, out size));
            Debug.Assert(size == sizeof(long));

            return doc;
        }     

        public static IEnumerable<(Slice Key, BlittableJsonReaderObject Value)> ReadValuesStartingWith(
            TransactionOperationContext context, string startsWithKey)
        {
            var startsWithKeyLower = startsWithKey.ToLowerInvariant();
            using (Slice.From(context.Allocator, startsWithKeyLower, out Slice startsWithSlice))
            {
                var items = context.Transaction.InnerTransaction.OpenTable(ItemsSchema, Items);

                foreach (var holder in items.SeekByPrimaryKeyPrefix(startsWithSlice, Slices.Empty, 0))
                {
                    var reader = holder.Value.Reader;
                    var size = GetDataAndEtagTupleFromReader(context, reader, out BlittableJsonReaderObject doc, out long _);
                    Debug.Assert(size == sizeof(long));

                    yield return (holder.Key, doc);
                }
            }
        }

        private static unsafe int GetDataAndEtagTupleFromReader(TransactionOperationContext context, TableValueReader reader, out BlittableJsonReaderObject doc, out long etag)
        {
            var ptr = reader.Read(2, out int size);
            doc = new BlittableJsonReaderObject(ptr, size, context);

            etag = Bits.SwapBytes(*(long*)reader.Read(3, out size));
            Debug.Assert(size == sizeof(long));
            return size;
        }

        public override async Task<Stream> ConnectToPeer(string url, string apiKey)
        {
            if (url == null) throw new ArgumentNullException(nameof(url));
            if (_parent == null) throw new InvalidOperationException("Cannot connect to peer without a parent");
            if (_parent.IsEncrypted && url.StartsWith("https:", StringComparison.OrdinalIgnoreCase) == false)
                throw new InvalidOperationException($"Failed to connect to node {url}. Connections from encrypted store must use HTTPS.");

            var info = await ReplicationUtils.GetTcpInfoAsync(url, "Rachis.Server", apiKey, "Cluster");
            var authenticator = new ApiKeyAuthenticator();

            var tcpInfo = new Uri(info.Url);
            var tcpClient = new TcpClient();
            Stream stream = null;
            try
            {
                await tcpClient.ConnectAsync(tcpInfo.Host, tcpInfo.Port);
                stream = await TcpUtils.WrapStreamWithSslAsync(tcpClient, info);

                using (ContextPoolForReadOnlyOperations.AllocateOperationContext(out JsonOperationContext context))
                {
                    var apiToken = await authenticator.GetAuthenticationTokenAsync(apiKey, url, context);
                    var msg = new DynamicJsonValue
                    {
                        [nameof(TcpConnectionHeaderMessage.DatabaseName)] = null,
                        [nameof(TcpConnectionHeaderMessage.Operation)] = TcpConnectionHeaderMessage.OperationTypes.Cluster,
                        [nameof(TcpConnectionHeaderMessage.AuthorizationToken)] = apiToken,
                    };
                    using (var writer = new BlittableJsonTextWriter(context, stream))
                    using (var msgJson = context.ReadObject(msg, "message"))
                    {
                        context.Write(writer, msgJson);
                    }
                    using (var response = context.ReadForMemory(stream, "cluster-ConnectToPeer-header-response"))
                    {

                        var reply = JsonDeserializationServer.TcpConnectionHeaderResponse(response);
                        switch (reply.Status)
                        {
                            case TcpConnectionHeaderResponse.AuthorizationStatus.Forbidden:
                                throw AuthorizationException.Forbidden("Server");
                            case TcpConnectionHeaderResponse.AuthorizationStatus.Success:
                                break;
                            default:
                                throw AuthorizationException.Unauthorized(reply.Status, "Server");
                        }
                    }
                }
                return stream;
            }
            catch (Exception)
            {
                stream?.Dispose();
                tcpClient.Dispose();
                throw;
            }
        }

        public override void OnSnapshotInstalled(TransactionOperationContext context, long lastIncludedIndex)
        {
            var listOfDatabaseName = GetDatabaseNames(context).ToList();
            //There is potentially a lot of work to be done here so we are responding to the change on a separate task.
            var onDatabaseChanged = DatabaseChanged;
            if (onDatabaseChanged != null)
            {
                _rachisLogIndexNotifications.NotifyListenersAbout(lastIncludedIndex);
                TaskExecutor.Execute(_ =>
                {
                    foreach (var db in listOfDatabaseName)
                        onDatabaseChanged.Invoke(this, (db, lastIncludedIndex, "SnapshotInstalled"));
                }, null);
            }
        }
    }

    public class RachisLogIndexNotifications
    {

        private long _lastModifiedIndex;
        private readonly AsyncManualResetEvent _notifiedListeners;

        public RachisLogIndexNotifications(CancellationToken token)
        {
            _notifiedListeners = new AsyncManualResetEvent(token);
        }

        public async Task WaitForIndexNotification(long index, TimeSpan? timeout = null)
        {
            while (true)
            {
                // first get the task, then wait on it
                var waitAsync = timeout.HasValue == false ? _notifiedListeners.WaitAsync() : _notifiedListeners.WaitAsync(timeout.Value);

                if (index <= Volatile.Read(ref _lastModifiedIndex))
                    break;

                if (await waitAsync == false)
                {
                    ThrowTimeoutException(timeout ?? TimeSpan.MaxValue, index, _lastModifiedIndex);
                }
            }
        }

        private static void ThrowTimeoutException(TimeSpan value, long index, long lastModifiedIndex)
        {
            throw new TimeoutException($"Waited for {value} but didn't get index notification for {index}. " +
                                       $"Last commit index is: {lastModifiedIndex}.");
        }

        public void NotifyListenersAbout(long index)
        {
            var lastModified = _lastModifiedIndex;
             while (index > lastModified)
            {
                lastModified = Interlocked.CompareExchange(ref _lastModifiedIndex, index, lastModified);
            }
            _notifiedListeners.SetAndResetAtomically();
        }
    }
}