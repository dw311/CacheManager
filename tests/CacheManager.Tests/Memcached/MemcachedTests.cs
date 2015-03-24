﻿using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CacheManager.Core;
using CacheManager.Core.Cache;
using CacheManager.Core.Configuration;
using CacheManager.Memcached;
using CacheManager.SystemRuntimeCaching;
using CacheManager.Tests.TestCommon;
using FluentAssertions;
using Xunit;

namespace CacheManager.Tests.SystemRuntimeCaching
{
    /// <summary>
    /// To run the memcached test, run the bat files under /memcached before executing the tests!
    /// </summary>
    [ExcludeFromCodeCoverage]
    public class MemcachedTests
    {
        [Fact]
        public void Memcached_Ctor()
        {
            // arrange
            // act
            Action act = () => CacheFactory.Build<IAmNotSerializable>("myCache", settings =>
                            {
                                settings.WithUpdateMode(CacheUpdateMode.Full)
                                    .WithHandle<MemcachedCacheHandle<IAmNotSerializable>>("default")
                                    .WithExpiration(ExpirationMode.Absolute, TimeSpan.FromSeconds(1));
                            });
            // assert
            act.ShouldThrow<TargetInvocationException>()
                .WithInnerException<InvalidOperationException>()
                .WithInnerMessage("To use memcached*IAmNotSerializable is not*");
        }

        [Fact]
        [Trait("IntegrationTest", "Memcached")]
        public void Memcached_KeySizeLimit()
        {
            // arrange
            var longKey = string.Join("", Enumerable.Repeat("a", 300));

            var item = new CacheItem<string>(longKey, "something");
            var cache = CacheFactory.Build<string>("myCache", settings =>
            {
                settings.WithUpdateMode(CacheUpdateMode.Full)
                    .WithHandle<MemcachedCacheHandle<string>>("default")
                    .WithExpiration(ExpirationMode.Absolute, TimeSpan.FromSeconds(1));
            });

            // act
            using (cache)
            {
                cache.Remove(item.Key);
                Func<bool> act = () => cache.Add(item);
                Func<string> act2 = () => cache[item.Key];

                // assert
                act().Should().BeTrue();
                act2().Should().Be(item.Value);
            }
        }

        [Fact]
        [Trait("IntegrationTest", "Memcached")]
        public void Memcached_KeySizeLimit_WithRegion()
        {
            // arrange
            var longKey = string.Join("", Enumerable.Repeat("a", 300));

            var item = new CacheItem<string>(longKey, "something", "someRegion");
            var cache = CacheFactory.Build<string>("myCache", settings =>
            {
                settings.WithUpdateMode(CacheUpdateMode.Full)
                    .WithHandle<MemcachedCacheHandle<string>>("default")
                    .WithExpiration(ExpirationMode.Absolute, TimeSpan.FromMinutes(1));
            });

            // act
            using (cache)
            {
                cache.Remove(item.Key, item.Region);
                Func<bool> act = () => cache.Add(item);
                Func<string> act2 = () => cache[item.Key, item.Region];

                // assert
                act().Should().BeTrue();
                act2().Should().Be(item.Value);
            }
        }

        [Fact]
        [Trait("IntegrationTest", "Memcached")]
        public void Memcached_Absolute_DoesExpire()
        {
            // arrange
            var item = new CacheItem<object>("key", "something");
            // act
            var cache = CacheFactory.Build("myCache", settings =>
            {
                settings.WithUpdateMode(CacheUpdateMode.Full)
                    .WithHandle<MemcachedCacheHandle<object>>("default")
                    .WithExpiration(ExpirationMode.Absolute, TimeSpan.FromSeconds(1));
            });

            using (cache)
            {
                cache.Clear();

                for (int i = 0; i < 3; i++)
                {
                    // act
                    var result = cache.Add("key" + i, "value" + i);

                    // assert
                    result.Should().BeTrue();
                    Thread.Sleep(10);
                    var value = cache.GetCacheItem("key" + i);
                    value.Should().NotBeNull();

                    Thread.Sleep(2000);
                    var valueExpired = cache.GetCacheItem("key" + i);
                    valueExpired.Should().BeNull();
                }
            }
        }

        [Fact]
        [Trait("IntegrationTest", "Memcached")]
        public void Memcached_RaceCondition_WithoutCasHandling()
        {
            // arrange
            using (var cache = CacheFactory.Build<RaceConditionTestElement>("myCache", settings =>
            {
                settings.WithUpdateMode(CacheUpdateMode.Full)
                    .WithHandle<MemcachedCacheHandle<RaceConditionTestElement>>("default")
                    .WithExpiration(ExpirationMode.Absolute, TimeSpan.FromMinutes(20));
            }))
            {
                cache.Remove("myCounter");
                cache.Add("myCounter", new RaceConditionTestElement() { Counter = 0 });
                int numThreads = 5;
                int iterations = 10;
                int numInnerIterations = 10;

                // act
                ThreadTestHelper.Run(() =>
                {
                    for (int i = 0; i < numInnerIterations; i++)
                    {
                        var val = cache.Get("myCounter");
                        val.Should().NotBeNull();
                        val.Counter++;

                        cache.Put("myCounter", val);
                    }
                }, numThreads, iterations);

                // assert
                Thread.Sleep(10);
                var result = cache.Get("myCounter");
                result.Should().NotBeNull();
                Trace.TraceInformation("Counter increased to " + result.Counter);
                result.Counter.Should().NotBe(numThreads * numInnerIterations * iterations);
            }
        }

        [Fact]
        [Trait("IntegrationTest", "Memcached")]
        public void Memcached_NoRaceCondition_WithCasHandling()
        {
            // arrange
            using (var cache = CacheFactory.Build<RaceConditionTestElement>("myCache", settings =>
            {
                settings.WithUpdateMode(CacheUpdateMode.Full)
                    .WithHandle<MemoryCacheHandle<RaceConditionTestElement>>("default")
                        .WithExpiration(ExpirationMode.Absolute, TimeSpan.FromMilliseconds(1))
                    .And
                    .WithHandle<MemcachedCacheHandle<RaceConditionTestElement>>("default")
                        .WithExpiration(ExpirationMode.Absolute, TimeSpan.FromSeconds(10));
            }))
            {
                cache.Remove("myCounter");
                cache.Add("myCounter", new RaceConditionTestElement() { Counter = 0 });
                int numThreads = 5;
                int iterations = 10;
                int numInnerIterations = 10;
                int countCasModifyCalls = 0;

                // act
                ThreadTestHelper.Run(() =>
                {
                    for (int i = 0; i < numInnerIterations; i++)
                    {
                        cache.Update("myCounter", (value) =>
                        {
                            value.Counter++;
                            Interlocked.Increment(ref countCasModifyCalls);
                            return value;
                        }, new UpdateItemConfig(50, VersionConflictHandling.EvictItemFromOtherCaches));
                    }
                }, numThreads, iterations);

                // assert
                Thread.Sleep(10);
                var result = cache.Get("myCounter");
                result.Should().NotBeNull();
                Trace.WriteLine("Counter increased to " + result.Counter + " cas calls needed " + countCasModifyCalls);
                result.Counter.Should().Be(numThreads * numInnerIterations * iterations, "counter should be exactly the expected value");
                countCasModifyCalls.Should().BeGreaterThan((int)result.Counter, "we expect many version collisions, so cas calls should be way higher then the count result");
            }
        }

        [Fact]
        [Trait("IntegrationTest", "Memcached")]
        public void Memcached_NoRaceCondition_WithCasHandling_WithRegion()
        {
            // arrange
            using (var cache = CacheFactory.Build<RaceConditionTestElement>("myCache", settings =>
            {
                settings.WithUpdateMode(CacheUpdateMode.Full)
                    //.WithHandle<MemoryCacheHandle<RaceConditionTestElement>>("default")
                    //    .WithExpiration(ExpirationMode.Absolute, TimeSpan.FromSeconds(1))
                    //.And
                    .WithHandle<MemcachedCacheHandle<RaceConditionTestElement>>("default")
                        .WithExpiration(ExpirationMode.Absolute, TimeSpan.FromMinutes(10));
            }))
            {
                var region = "region";
                var key = "myKey";
                cache.Remove(key, region);
                cache.Add(key, new RaceConditionTestElement() { Counter = 0 }, region);
                int numThreads = 5;
                int iterations = 10;
                int numInnerIterations = 10;
                int countCasModifyCalls = 0;

                // act
                ThreadTestHelper.Run(() =>
                {
                    for (int i = 0; i < numInnerIterations; i++)
                    {
                        cache.Update(key, region, (value) =>
                        {
                            value.Counter++;
                            Interlocked.Increment(ref countCasModifyCalls);
                            return value;
                        });
                    }
                }, numThreads, iterations);

                // assert
                Thread.Sleep(10);
                var result = cache.Get(key, region);
                result.Should().NotBeNull();
                Trace.TraceInformation("Counter increased to " + result.Counter + " cas calls needed " + countCasModifyCalls);
                result.Counter.Should().Be(numThreads * numInnerIterations * iterations, "counter should be exactly the expected value");
                countCasModifyCalls.Should().BeGreaterThan((int)result.Counter, "we expect many version collisions, so cas calls should be way higher then the count result");
            }
        }

        [Fact]
        [Trait("IntegrationTest", "Memcached")]
        public void Memcached_NoRaceCondition_WithCasButTooFiewRetries()
        {
            // arrange
            using (var cache = CacheFactory.Build<RaceConditionTestElement>("myCache", settings =>
            {
                settings.WithUpdateMode(CacheUpdateMode.Full)
                    .WithHandle<MemcachedCacheHandle<RaceConditionTestElement>>("default")
                        .WithExpiration(ExpirationMode.Absolute, TimeSpan.FromHours(10));
            }))
            {
                cache.Remove("myCounter");
                cache.Add("myCounter", new RaceConditionTestElement() { Counter = 0 });
                int numThreads = 5;
                int iterations = 10;
                int numInnerIterations = 10;
                int countCasModifyCalls = 0;
                int retries = 0;

                // act
                ThreadTestHelper.Run(() =>
                {
                    for (int i = 0; i < numInnerIterations; i++)
                    {
                        cache.Update("myCounter", (value) =>
                        {
                            value.Counter++;
                            Interlocked.Increment(ref countCasModifyCalls);
                            return value;
                        }, new UpdateItemConfig(retries, VersionConflictHandling.EvictItemFromOtherCaches));
                    }
                }, numThreads, iterations);

                // assert
                Thread.Sleep(10);
                var result = cache.Get("myCounter");
                result.Should().NotBeNull();
                Trace.TraceInformation("Counter increased to " + result.Counter + " cas calls needed " + countCasModifyCalls);
                result.Counter.Should().BeLessThan(numThreads * numInnerIterations * iterations, 
                    "counter should NOT be exactly the expected value");
                countCasModifyCalls.Should().Be(numThreads * numInnerIterations * iterations, 
                    "with one try, we exactly one update call per iteration");
            }
        }

        [Fact]
        public void Memcached_Update_ItemNotAdded()
        {
            // arrange
            using (var cache = CacheFactory.Build<RaceConditionTestElement>("myCache", settings =>
            {
                settings.WithUpdateMode(CacheUpdateMode.Full)
                    .WithHandle<MemcachedCacheHandle<RaceConditionTestElement>>("default")
                    .WithExpiration(ExpirationMode.Absolute, TimeSpan.FromMinutes(20));
            }))
            {
                // act
                Func<bool> act = () => cache.Update(Guid.NewGuid().ToString(), item => item);

                // assert
                act().Should().BeFalse("Item has not been added to the cache");
            }
        }
    }


    [Serializable]
    [ExcludeFromCodeCoverage]
    public class RaceConditionTestElement
    {
        public RaceConditionTestElement()
        {
        }

        public long Counter { get; set; }
    }

    [ExcludeFromCodeCoverage]
    public class IAmNotSerializable
    {

    }
}