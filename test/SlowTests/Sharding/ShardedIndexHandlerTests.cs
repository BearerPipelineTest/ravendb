﻿using System.Linq;
using System.Threading.Tasks;
using FastTests.Sharding;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using SlowTests.Core.Utils.Entities;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Sharding
{
    public class ShardedIndexHandlerTests : ShardedTestBase
    {
        public ShardedIndexHandlerTests(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public async Task CanGetIndexStatistics()
        {
            using (var store = GetShardedDocumentStore())
            {
                using (var session = store.OpenAsyncSession())
                {
                    for (var i = 0; i < 10; i++)
                    {
                        var id = $"Raven/{i}";

                        var user = new User { Name = $"Raven-{i}" };
                        await session.StoreAsync(user, id);
                        await session.SaveChangesAsync();
                    }
                }

                await new UserIndex().ExecuteAsync(store);
               
                var indexStats = await store.Maintenance.ForNode("A").ForShardWithProxy(0).SendAsync(new GetIndexesStatisticsOperation());
                Assert.NotNull(indexStats);
                Assert.Equal(1, indexStats.Length);
                Assert.Equal("UserIndex", indexStats[0].Name);
                Assert.Equal(1, indexStats[0].Collections.Count);
                Assert.True(indexStats[0].Collections.ContainsKey("Users"));
            }
        }

        [Fact]
        public async Task CanGetIndexesStatus()
        {
            using (var store = GetShardedDocumentStore())
            {
                using (var session = store.OpenAsyncSession())
                {
                    for (var i = 0; i < 10; i++)
                    {
                        var id = $"Raven/{i}";

                        var user = new User { Name = $"Raven-{i}" };
                        await session.StoreAsync(user, id);
                        await session.SaveChangesAsync();
                    }
                }

                await new UserIndex().ExecuteAsync(store);

                var indexStats = await store.Maintenance.ForNode("A").ForShardWithProxy(0).SendAsync(new GetIndexingStatusOperation());
                Assert.NotNull(indexStats);
                Assert.Equal(IndexRunningStatus.Running, indexStats.Status);
                Assert.Equal(1, indexStats.Indexes.Length);
                Assert.Equal("UserIndex", indexStats.Indexes[0].Name);
            }
        }

        private class UserIndex : AbstractIndexCreationTask<User>
        {
            public UserIndex()
            {
                Map = users => from user in users
                               select new
                               {
                                   user.Name
                               };
            }
        }
    }
}