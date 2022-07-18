﻿using System.Linq;
using FastTests;
using Newtonsoft.Json.Linq;
using Raven.Client.Exceptions;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Issues
{
    public class RavenDB_12059 : RavenTestBase
    {
        public RavenDB_12059(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public void Query_without_alias_should_properly_fail()
        {
            using (var store = GetDocumentStore())
            {
                Samples.CreateNorthwindDatabase(store);
                using (var session = store.OpenSession())
                {
                    Assert.True(session.Advanced.RawQuery<JObject>("match (Orders as o where id() = 'orders/825-A') select o.Company").ToArray().Length > 0);
                    Assert.Throws<InvalidQueryException>(() => session.Advanced.RawQuery<JObject>("match (Orders as o where id() = 'orders/825-A') select Company").ToArray());

                    Assert.Throws<InvalidQueryException>(() => session.Advanced.RawQuery<JObject>("match (Orders as o where id() = 'orders/825-A') select o.Product, Company").ToArray());
                }
            }
        }
    }
}
