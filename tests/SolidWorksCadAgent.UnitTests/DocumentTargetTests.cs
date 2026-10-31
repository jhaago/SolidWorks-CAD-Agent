using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.SolidWorksBridge;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class DocumentTargetTests
    {
        private static object Context()
        {
            var type = typeof(SolidWorksBridgeFacade).Assembly.GetType("SolidWorksCadAgent.SolidWorksBridge.Session.SolidWorksDocumentContext");
            Assert.IsNotNull(type, "Document commands need a bound document context.");
            return Activator.CreateInstance(type, true);
        }
        private static object Call(object context, string method, params object[] args) =>
            context.GetType().GetMethod(method).Invoke(context, args);
        private static void Reject(object context, object active)
        {
            var exception = Assert.ThrowsException<TargetInvocationException>(() => Call(context, "RequireActive", active));
            Assert.AreEqual("DocumentTargetException", exception.InnerException.GetType().Name);
        }
        [TestMethod]
        public void SwitchingDocuments_RejectsUnrelatedDocumentAndKeepsOriginalBinding()
        {
            var context = Context();
            var original = new object();
            Call(context, "Bind", original);
            Reject(context, new object());
            Assert.AreSame(original, Call(context, "RequireActive", original));
        }
        [TestMethod]
        public void NoBoundDocument_RejectsExistingUserDocument()
        {
            Reject(Context(), new object());
        }
        [TestMethod]
        public void NewExecution_CannotInheritPreviousJobsDocument()
        {
            var context = Context();
            var original = new object();
            var job = Guid.NewGuid();
            Call(context, "BeginExecution", (Guid?)job);
            Call(context, "Bind", original);
            Call(context, "BeginExecution", (Guid?)job);
            Assert.AreSame(original, Call(context, "RequireActive", original));
            Call(context, "BeginExecution", (Guid?)Guid.NewGuid());
            Reject(context, original);
        }
        [TestMethod]
        public void ClosedDocument_RejectsDocumentThatBecomesActiveAfterClose()
        {
            var context = Context();
            Call(context, "Bind", new object());
            Call(context, "Clear");
            Reject(context, new object());
        }
    }
}
