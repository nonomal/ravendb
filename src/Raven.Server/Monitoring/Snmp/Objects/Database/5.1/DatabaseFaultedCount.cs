using Lextm.SharpSnmpLib;
using Raven.Server.ServerWide;

namespace Raven.Server.Monitoring.Snmp.Objects.Database
{
    public class DatabaseFaultedCount : DatabaseBase<Integer32>
    {
        public DatabaseFaultedCount(ServerStore serverStore)
            : base(serverStore, SnmpOids.Databases.General.FaultedCount)
        {
        }

        protected override Integer32 GetData()
        {
            return new Integer32(GetCount());
        }

        private int GetCount()
        {
            var count = 0;
            foreach (var kvp in ServerStore.DatabasesLandlord.DatabasesCache)
            {
                var databaseTask = kvp.Value;

                if (databaseTask == null)
                    continue;

                if (databaseTask.IsFaulted)
                    count++;
            }

            return count;
        }
    }
}
