﻿using System.Linq;
using System.Threading.Tasks;
using FastTests;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Issues
{
    public class RavenDB_16498 : RavenTestBase
    {
        public RavenDB_16498(ITestOutputHelper output) : base(output)
        {
        }

        private class User
        {
#pragma warning disable CS0649
            public string Email;
#pragma warning restore CS0649
        }

        private class User_Search : AbstractIndexCreationTask<User>
        {
            public User_Search()
            {
                Map = users =>
                    from u in users
                    select new { Email = u.Email + "---" };
            }
        }

        [Fact]
        public async Task StringConactOnNull()
        {
            using var store = GetDocumentStore();
            using (var s = store.OpenAsyncSession())
            {
                await s.StoreAsync(new User());
                await s.SaveChangesAsync();
            }

            await new User_Search().ExecuteAsync(store);
            Indexes.WaitForIndexing(store);

            using (var s = store.OpenAsyncSession())
            {
                var q = await s.Query<User, User_Search>()
                    .Where(x => x.Email == "---")
                    .ToListAsync();

                Assert.NotEmpty(q);
            }

        }
    }
}
