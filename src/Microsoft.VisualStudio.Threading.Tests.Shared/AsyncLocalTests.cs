﻿namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class AsyncLocalTests : TestBase
    {
        // Be VERY explicit about the type we're binding against since
        // there is now one in System.Threading and we want to be sure
        // we're testing OUR stuff not THEIRS.
        private Microsoft.VisualStudio.Threading.AsyncLocal<GenericParameterHelper> asyncLocal;

        public TestContext TestContext { get; set; }

        [TestInitialize]
        public void Initialize()
        {
            this.asyncLocal = new Microsoft.VisualStudio.Threading.AsyncLocal<GenericParameterHelper>();
            this.SetTestContext(this.TestContext);
        }

        [TestMethod]
        public void SetGetNoYield()
        {
            var value = new GenericParameterHelper();
            this.asyncLocal.Value = value;
            Assert.AreSame(value, this.asyncLocal.Value);
            this.asyncLocal.Value = null;
            Assert.IsNull(this.asyncLocal.Value);
        }

        [TestMethod]
        public async Task SetGetWithYield()
        {
            var value = new GenericParameterHelper();
            this.asyncLocal.Value = value;
            await Task.Yield();
            Assert.AreSame(value, this.asyncLocal.Value);
            this.asyncLocal.Value = null;
            Assert.IsNull(this.asyncLocal.Value);
        }

        [TestMethod]
        public async Task ForkedContext()
        {
            var value = new GenericParameterHelper();
            this.asyncLocal.Value = value;
            await Task.WhenAll(
                Task.Run(delegate
                {
                    Assert.AreSame(value, this.asyncLocal.Value);
                    this.asyncLocal.Value = null;
                    Assert.IsNull(this.asyncLocal.Value);
                }),
                Task.Run(delegate
                {
                    Assert.AreSame(value, this.asyncLocal.Value);
                    this.asyncLocal.Value = null;
                    Assert.IsNull(this.asyncLocal.Value);
                }));

            Assert.AreSame(value, this.asyncLocal.Value);
            this.asyncLocal.Value = null;
            Assert.IsNull(this.asyncLocal.Value);
        }

        [TestMethod, Timeout(TestTimeout)]
        public async Task IndependentValuesBetweenContexts()
        {
            await IndependentValuesBetweenContextsHelper<GenericParameterHelper>();
            await IndependentValuesBetweenContextsHelper<object>();
        }

        [TestMethod]
        public void SetNewValuesRepeatedly()
        {
            for (int i = 0; i < 10; i++)
            {
                var value = new GenericParameterHelper();
                this.asyncLocal.Value = value;
                Assert.AreSame(value, this.asyncLocal.Value);
            }

            this.asyncLocal.Value = null;
            Assert.IsNull(this.asyncLocal.Value);
        }

        [TestMethod]
        public void SetSameValuesRepeatedly()
        {
            var value = new GenericParameterHelper();
            for (int i = 0; i < 10; i++)
            {
                this.asyncLocal.Value = value;
                Assert.AreSame(value, this.asyncLocal.Value);
            }

            this.asyncLocal.Value = null;
            Assert.IsNull(this.asyncLocal.Value);
        }

        [TestMethod, TestCategory("GC")]
        public void SurvivesGC()
        {
            var value = new GenericParameterHelper(5);
            this.asyncLocal.Value = value;
            Assert.AreSame(value, this.asyncLocal.Value);

            GC.Collect();
            Assert.AreSame(value, this.asyncLocal.Value);

            value = null;
            GC.Collect();
            Assert.AreEqual(5, this.asyncLocal.Value.Data);
        }

        [TestMethod]
        public void NotDisruptedByTestContextWriteLine()
        {
            var value = new GenericParameterHelper();
            this.asyncLocal.Value = value;

            // TestContext.WriteLine causes the CallContext to be serialized.
            // When a .testsettings file is applied to the test runner, the
            // original contents of the CallContext are replaced with a
            // serialize->deserialize clone, which can break the reference equality
            // of the objects stored in the AsyncLocal class's private fields
            // if it's not done properly.
            this.Logger.WriteLine("Foobar");

            Assert.IsNotNull(this.asyncLocal.Value);
            Assert.AreSame(value, this.asyncLocal.Value);
        }

        [TestMethod]
        public void ValuePersistsAcrossExecutionContextChanges()
        {
            var jtLocal = new AsyncLocal<object>();
            jtLocal.Value = 1;
            Func<Task> asyncMethod = async delegate
            {
                SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
                Assert.AreEqual(1, jtLocal.Value);
                jtLocal.Value = 3;
                Assert.AreEqual(3, jtLocal.Value);
                await TaskScheduler.Default;
                Assert.AreEqual(3, jtLocal.Value);
            };
            asyncMethod().GetAwaiter().GetResult();

            Assert.AreEqual(1, jtLocal.Value);
        }

        [TestMethod, TestCategory("Performance")]
        public void AsyncLocalPerfTest()
        {
            var values = Enumerable.Range(1, 50000).Select(n => new GenericParameterHelper(n)).ToArray();

            var writes = Stopwatch.StartNew();
            for (int i = 0; i < values.Length; i++)
            {
                this.asyncLocal.Value = values[0];
            }

            writes.Stop();

            var reads = Stopwatch.StartNew();
            for (int i = 0; i < values.Length; i++)
            {
                var value = this.asyncLocal.Value;
            }

            reads.Stop();

            // We don't actually validate the perf here. We just print out the results.
            Console.WriteLine("Saving {0} values took {1} ms", values.Length, writes.ElapsedMilliseconds);
            Console.WriteLine("Reading {0} values took {1} ms", values.Length, reads.ElapsedMilliseconds);
        }

        [TestMethod]
        public void CallAcrossAppDomainBoundariesWithNonSerializableData()
        {
            var otherDomain = AppDomain.CreateDomain("test domain");
            try
            {
                var proxy = (OtherDomainProxy)otherDomain.CreateInstanceFromAndUnwrap(Assembly.GetExecutingAssembly().Location, typeof(OtherDomainProxy).FullName);

                // Verify we can call it first.
                proxy.SomeMethod(AppDomain.CurrentDomain.Id);

                // Verify we can call it while AsyncLocal has a non-serializable value.
                var value = new GenericParameterHelper();
                this.asyncLocal.Value = value;
                proxy.SomeMethod(AppDomain.CurrentDomain.Id);
                Assert.AreSame(value, this.asyncLocal.Value);

                // Nothing permanently damaged in the ability to set/get values.
                this.asyncLocal.Value = null;
                this.asyncLocal.Value = value;
                Assert.AreSame(value, this.asyncLocal.Value);

                // Verify we can call it after clearing the value.
                this.asyncLocal.Value = null;
                proxy.SomeMethod(AppDomain.CurrentDomain.Id);
            }
            finally
            {
                AppDomain.Unload(otherDomain);
            }
        }

        private static async Task IndependentValuesBetweenContextsHelper<T>() where T : class, new()
        {
            var asyncLocal = new AsyncLocal<T>();
            var player1 = new AsyncAutoResetEvent();
            var player2 = new AsyncAutoResetEvent();
            await Task.WhenAll(
                Task.Run(async delegate
                {
                    Assert.IsNull(asyncLocal.Value);
                    var value = new T();
                    asyncLocal.Value = value;
                    Assert.AreSame(value, asyncLocal.Value);
                    player1.Set();
                    await player2.WaitAsync();
                    Assert.AreSame(value, asyncLocal.Value);
                }),
                Task.Run(async delegate
                {
                    await player1.WaitAsync();
                    Assert.IsNull(asyncLocal.Value);
                    var value = new T();
                    asyncLocal.Value = value;
                    Assert.AreSame(value, asyncLocal.Value);
                    asyncLocal.Value = null;
                    player2.Set();
                }));

            Assert.IsNull(asyncLocal.Value);
        }

        private class OtherDomainProxy : MarshalByRefObject
        {
            internal void SomeMethod(int callingAppDomainId)
            {
                Assert.AreNotEqual(callingAppDomainId, AppDomain.CurrentDomain.Id, "AppDomain boundaries not crossed.");
            }
        }
    }
}