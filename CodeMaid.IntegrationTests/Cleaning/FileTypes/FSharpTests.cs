﻿using EnvDTE;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VSSDK.Tools.VsIdeTesting;
using SteveCadwallader.CodeMaid.IntegrationTests.Helpers;
using SteveCadwallader.CodeMaid.Logic.Cleaning;
using SteveCadwallader.CodeMaid.Properties;
using System;

namespace SteveCadwallader.CodeMaid.IntegrationTests.Cleaning.FileTypes
{
    [TestClass]
    [DeploymentItem(@"Cleaning\FileTypes\Data\FSharp.fs", "Data")]
    public class FSharpTests
    {
        #region Setup

        private static CodeCleanupAvailabilityLogic _codeCleanupAvailabilityLogic;
        private ProjectItem _projectItem;

        [ClassInitialize]
        public static void ClassInitialize(TestContext testContext)
        {
            _codeCleanupAvailabilityLogic = CodeCleanupAvailabilityLogic.GetInstance(TestEnvironment.Package);
            Assert.IsNotNull(_codeCleanupAvailabilityLogic);
        }

        [TestInitialize]
        public void TestInitialize()
        {
            TestEnvironment.CommonTestInitialize();
            _projectItem = TestEnvironment.LoadFileIntoProject(@"Data\FSharp.fs");
        }

        [TestCleanup]
        public void TestCleanup()
        {
            TestEnvironment.RemoveFromProject(_projectItem);
        }

        #endregion Setup

        #region Tests

        [TestMethod]
        [HostType("VS IDE")]
        public void CleaningFileTypesFSharp_EnablesForDocument()
        {
            Settings.Default.Cleaning_IncludeFSharp = true;

            UIThreadInvoker.Invoke(new Action(() =>
            {
                // Make sure the document is the active document for the environment.
                var document = TestOperations.GetActivatedDocument(_projectItem);
                Assert.AreEqual(document, TestEnvironment.Package.ActiveDocument);

                // Confirm the code cleanup availability logic is in the expected state.
                Assert.IsTrue(_codeCleanupAvailabilityLogic.CanCleanupDocument(document));
            }));
        }

        [TestMethod]
        [HostType("VS IDE")]
        public void CleaningFileTypesFSharp_EnablesForProjectItem()
        {
            Settings.Default.Cleaning_IncludeFSharp = true;

            UIThreadInvoker.Invoke(new Action(() =>
            {
                // Confirm the code cleanup availability logic is in the expected state.
                Assert.IsTrue(_codeCleanupAvailabilityLogic.CanCleanupProjectItem(_projectItem));
            }));
        }

        [TestMethod]
        [HostType("VS IDE")]
        public void CleaningFileTypesFSharp_DisablesForDocumentWhenSettingIsDisabled()
        {
            Settings.Default.Cleaning_IncludeFSharp = false;

            UIThreadInvoker.Invoke(new Action(() =>
            {
                // Make sure the document is the active document for the environment.
                var document = TestOperations.GetActivatedDocument(_projectItem);
                Assert.AreEqual(document, TestEnvironment.Package.ActiveDocument);

                // Confirm the code cleanup availability logic is in the expected state.
                Assert.IsFalse(_codeCleanupAvailabilityLogic.CanCleanupDocument(document));
            }));
        }

        [TestMethod]
        [HostType("VS IDE")]
        public void CleaningFileTypesFSharp_DisablesForProjectItemWhenSettingIsDisabled()
        {
            Settings.Default.Cleaning_IncludeFSharp = false;

            UIThreadInvoker.Invoke(new Action(() =>
            {
                // Confirm the code cleanup availability logic is in the expected state.
                Assert.IsFalse(_codeCleanupAvailabilityLogic.CanCleanupProjectItem(_projectItem));
            }));
        }

        #endregion Tests
    }
}