﻿using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Exceptions;
using Raven.Client.ServerWide.Operations;
using Raven.Server.Utils;
using SlowTests.Cluster;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace StressTests.Cluster
{
    public class ClusterIndexNotificationsTestStress : ClusterTestBase
    {
        public ClusterIndexNotificationsTestStress(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public async Task ShouldThrowTimeoutException()
        {
            DebuggerAttachedTimeout.DisableLongTimespan = true;

            using (var store = GetDocumentStore(new Options { DeleteDatabaseOnDispose = true, Path = NewDataPath() }))
            using (var store2 = GetDocumentStore())
            {
                var documentDatabase = await GetDatabase(store.Database);
                var testingStuff = documentDatabase.ForTestingPurposesOnly();

                using (testingStuff.CallDuringDocumentDatabaseInternalDispose(() =>
                {
                    var sw = Stopwatch.StartNew();
                    while (sw.Elapsed < TimeSpan.FromSeconds(18))
                        Thread.Sleep(1000);
                }))
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                    var task = ClusterIndexNotificationsTest.BackgroundWorkAsync(store2, cts);

                    await ClusterIndexNotificationsTest.WaitForIndexCreationAsync(store2, cts.Token);

                    var e = await Assert.ThrowsAsync<RavenException>(async () =>
                    {
                        var r = await store.Maintenance.Server.SendAsync(new ToggleDatabasesStateOperation(store.Database, true), cts.Token);
                        throw new InvalidOperationException($"Expected to fail, but got: Name={r.Name}, Disabled={r.Disabled}, Success={r.Success}, Reason={r.Reason}");
                    });
                    Assert.True(e.InnerException is TimeoutException);

                    cts.Cancel();

                    try
                    {
                        task.Wait(TimeSpan.FromSeconds(60));
                    }
                    catch
                    {
                    }
                }
            }
        }
    }
}
