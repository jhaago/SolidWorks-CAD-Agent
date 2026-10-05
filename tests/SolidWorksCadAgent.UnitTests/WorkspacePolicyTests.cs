using System;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class WorkspacePolicyTests
    {
        private static Type GetPolicyType()
        {
            return Type.GetType(
                "SolidWorksCadAgent.Core.Workspace.WorkspacePolicy, SolidWorksCadAgent.Core",
                throwOnError: false);
        }

        private static object CreatePolicy(string root)
        {
            var type = GetPolicyType();
            Assert.IsNotNull(type, "WorkspacePolicy must exist.");
            return Activator.CreateInstance(type, root);
        }

        private static string ResolveForWrite(object policy, string path, bool allowOverwrite)
        {
            var method = policy.GetType().GetMethod("ResolveForWrite", new[] { typeof(string), typeof(bool) });
            Assert.IsNotNull(method, "ResolveForWrite(string,bool) must exist.");
            return (string)method.Invoke(policy, new object[] { path, allowOverwrite });
        }

        private static void AssertWorkspacePolicyFailure(Action action)
        {
            try
            {
                action();
                Assert.Fail("Expected WorkspacePolicyException.");
            }
            catch (TargetInvocationException ex)
            {
                Assert.IsNotNull(ex.InnerException);
                Assert.AreEqual("WorkspacePolicyException", ex.InnerException.GetType().Name);
            }
        }

        [TestMethod]
        public void ResolveForWrite_TraversalOutsideWorkspace_Throws()
        {
            var root = CreateTemporaryWorkspace();
            try
            {
                var policy = CreatePolicy(root);
                AssertWorkspacePolicyFailure(() => ResolveForWrite(policy, @"..\secret.sldprt", false));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public void ResolveForWrite_SiblingPrefix_Throws()
        {
            var root = CreateTemporaryWorkspace();
            try
            {
                var policy = CreatePolicy(root);
                var sibling = root + "-Evil";
                AssertWorkspacePolicyFailure(() => ResolveForWrite(policy, Path.Combine(sibling, "x.sldprt"), false));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public void ResolveForWrite_ExistingFileWithoutApproval_Throws()
        {
            var root = CreateTemporaryWorkspace();
            try
            {
                var target = Path.Combine(root, "existing.sldprt");
                File.WriteAllText(target, "existing");
                var policy = CreatePolicy(root);

                AssertWorkspacePolicyFailure(() => ResolveForWrite(policy, "existing.sldprt", false));
                Assert.AreEqual(Path.GetFullPath(target), ResolveForWrite(policy, "existing.sldprt", true));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public void ResolveForWrite_UnsupportedExtension_Throws()
        {
            var root = CreateTemporaryWorkspace();
            try
            {
                var policy = CreatePolicy(root);
                AssertWorkspacePolicyFailure(() => ResolveForWrite(policy, "payload.exe", false));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static string CreateTemporaryWorkspace()
        {
            var root = Path.Combine(Path.GetTempPath(), "SolidWorksCadAgentTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        [DataTestMethod]
        [DataRow(false, 0)]
        [DataRow(true, 0)]
        [DataRow(false, 1)]
        [DataRow(true, 1)]
        [DataRow(false, 2)]
        [DataRow(true, 2)]
        public void JunctionOutsideWorkspace_IsRejectedForReadAndWrite(bool read, int rootDepth)
        {
            var root = CreateTemporaryWorkspace();
            var outside = CreateTemporaryWorkspace();
            var link = Path.Combine(root, "redirect");
            try
            {
                File.WriteAllText(Path.Combine(outside, "existing.sldprt"), "outside");
                Directory.CreateDirectory(Path.Combine(outside, "workspace"));
                File.WriteAllText(Path.Combine(outside, "workspace", "existing.sldprt"), "outside");
                using (var process = Process.Start(new ProcessStartInfo("cmd.exe",
                    "/c mklink /J \"" + link + "\" \"" + outside + "\"")
                    { UseShellExecute = false, CreateNoWindow = true }))
                {
                    process.WaitForExit();
                    Assert.AreEqual(0, process.ExitCode, "Junction fixture creation failed.");
                }
                var policyRoot = rootDepth == 0 ? root : rootDepth == 1 ? link : Path.Combine(link, "workspace");
                var prefix = rootDepth == 0 ? @"redirect\" : string.Empty;
                var policy = CreatePolicy(policyRoot);
                if (read)
                    AssertWorkspacePolicyFailure(() => policy.GetType().GetMethod("ResolveForRead")
                        .Invoke(policy, new object[] { prefix + "existing.sldprt" }));
                else
                    AssertWorkspacePolicyFailure(() => ResolveForWrite(policy, prefix + @"new\part.sldprt", false));
                Assert.AreEqual("outside", File.ReadAllText(Path.Combine(outside, "existing.sldprt")));
            }
            finally
            {
                // Remove the junction itself before cleaning either real fixture directory.
                if (Directory.Exists(link)) Directory.Delete(link);
                Directory.Delete(root, true);
                Directory.Delete(outside, true);
            }
        }
    }
}
