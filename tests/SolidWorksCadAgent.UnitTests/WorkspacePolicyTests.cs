using System;
using System.IO;
using System.Reflection;
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
    }
}
