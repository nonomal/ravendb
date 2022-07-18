﻿using System.Linq;
using FastTests;
using Orders;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Issues
{
    public class RavenDB_11671 : RavenTestBase
    {
        public RavenDB_11671(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public void Can_properly_parse_query()
        {
            using (var store = GetDocumentStore())
            {
                Samples.CreateNorthwindDatabase(store);
                using (var session = store.OpenSession())
                {
                    var orders = session.Query<Order>().ToList();
                    var results = session.Advanced.RawQuery<Order>("match (Employees as e)<-[Employee]-(Orders as o)-[Company]->(Companies as c) select o").ToArray();
                    Assert.Equal(orders,results);
                }
            }
        }
    }
}
