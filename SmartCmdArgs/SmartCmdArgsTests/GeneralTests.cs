﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Input;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VsSDK.IntegrationTestLibrary;
using Microsoft.VSSDK.Tools.VsIdeTesting;
using SmartCmdArgs;

namespace SmartCmdArgsTests
{
    [TestClass]
    public class GeneralTests : TestBase
    {
        [TestMethod]
        [HostType("VS IDE")]
        [TestProperty(VsIdeTestHostContants.TestPropertyName.RegistryHiveName, HiveName)]
        [TestProperty(VsIdeTestHostContants.TestPropertyName.HiveStartFlags, HiveStartArgs)]
        [TestProperty(VsIdeTestHostContants.TestPropertyName.RestartOptions, VsIdeTestHostContants.HostRestartOptions.Before)]
        public void CollectArgsFromExistingProjectConfigsTest()
        {
            Project project = CreateSolutionWithProject("CollectTestSolution", "CollectTestProject");

            List<string> startArgumentsForEachConfig = new List<string>();
            foreach (Configuration config in project.ConfigurationManager)
            {
                string startArguments = $"args for {config.ConfigurationName}";
                Debug.WriteLine($"Adding args '{startArguments}' to configuration '{config.ConfigurationName}'");
                startArgumentsForEachConfig.Add(startArguments);
                config.Properties.Item("StartArguments").Value = startArguments;
            }

            var package = (CmdArgsPackage)Utils.LoadPackage(new Guid(CmdArgsPackage.PackageGuidString));
            var argItems = package?.ToolWindowViewModel?.CurrentArgumentList?.DataCollection;
            Assert.IsNotNull(argItems);

            Assert.AreEqual(startArgumentsForEachConfig.Count, argItems.Count);

            foreach (var startArguments in startArgumentsForEachConfig)
            {
                var argItem = argItems.FirstOrDefault(item => item.Command == startArguments);
                
                Assert.IsNotNull(argItem);
                Assert.AreNotEqual(Guid.Empty, argItem.Id);
            }
        }

        [TestMethod]
        [HostType("VS IDE")]
        [TestProperty(VsIdeTestHostContants.TestPropertyName.RegistryHiveName, HiveName)]
        [TestProperty(VsIdeTestHostContants.TestPropertyName.HiveStartFlags, HiveStartArgs)]
        [TestProperty(VsIdeTestHostContants.TestPropertyName.RestartOptions, VsIdeTestHostContants.HostRestartOptions.Before)]
        [DeploymentItem("ConsoleApplicationVC.zip")]
        public void CollectArgsDistinctFromExistingProjectConfigsTest()
        {
            const string startArguments = "same args in every config";

            Project project = CreateSolutionWithProject();

            foreach (Configuration config in project.ConfigurationManager)
            {
                Debug.WriteLine($"Adding args '{startArguments}' to configuration '{config.ConfigurationName}'");
                config.Properties.Item("StartArguments").Value = startArguments;
            }



            var package = (CmdArgsPackage)Utils.LoadPackage(new Guid(CmdArgsPackage.PackageGuidString));
            var argItems = package?.ToolWindowViewModel?.CurrentArgumentList?.DataCollection;
            Assert.IsNotNull(argItems);

            Assert.AreEqual(1, argItems.Count);

            var argItem = argItems[0];
            Assert.AreNotEqual(Guid.Empty, argItem.Id);
            Assert.AreEqual(startArguments, argItem.Command);
        }

        [TestMethod]
        [HostType("VS IDE")]
        [TestProperty(VsIdeTestHostContants.TestPropertyName.RegistryHiveName, HiveName)]
        [TestProperty(VsIdeTestHostContants.TestPropertyName.HiveStartFlags, HiveStartArgs)]
        [TestProperty(VsIdeTestHostContants.TestPropertyName.RestartOptions, VsIdeTestHostContants.HostRestartOptions.Before)]
        public void AddNewArgLineViaCommandTest()
        {
            CreateSolutionWithProject();

            var package = (CmdArgsPackage)Utils.LoadPackage(new Guid(CmdArgsPackage.PackageGuidString));

            ICommand addCommand = package?.ToolWindowViewModel?.AddEntryCommand;
            Assert.IsNotNull(addCommand);

            InvokeInUIThread(() =>
            {
                Assert.IsTrue(package.ToolWindowViewModel.AddEntryCommand.CanExecute(null));
                addCommand.Execute(null);

                var argItems = package?.ToolWindowViewModel?.CurrentArgumentList?.DataCollection;
                Assert.IsNotNull(argItems);

                Assert.AreEqual(1, argItems.Count);

                var argItem = argItems[0];
                Assert.AreNotEqual(Guid.Empty, argItem.Id);
                Assert.AreEqual("", argItem.Command);
            });
        }
    }
}

