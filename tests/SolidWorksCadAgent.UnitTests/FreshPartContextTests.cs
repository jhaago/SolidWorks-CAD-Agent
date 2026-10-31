using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class FreshPartContextTests
    {
        private static object Context()
        {
            const string name="SolidWorksCadAgent.SolidWorksBridge.Session.SolidWorksDocumentContext";
            var type=Type.GetType(name+", SolidWorksCadAgent.SolidWorksBridge",false)??Type.GetType(name+", SolidWorksCadAgent.PortableTests",true);
            return Activator.CreateInstance(type,true);
        }
        private static void Call(object context,string name,params object[] args)
        {var method=context.GetType().GetMethod(name);Assert.IsNotNull(method,name+" must exist on the production context.");method.Invoke(context,args);}
        private static FreshPartMutationGuard Guard(object context)
        {var property=context.GetType().GetProperty("FreshPartMutations");Assert.IsNotNull(property,"The bound document needs its own finishing eligibility state.");return (FreshPartMutationGuard)property.GetValue(context);}
        [TestMethod] public void GenericBindingCannotGrantNewPartPermission()
        {
            var context=Context();Call(context,"Bind",new object());
            Assert.ThrowsException<InvalidOperationException>(()=>Guard(context).RequireUnsaved(""));
            Call(context,"BindCreatedPart",new object());Guard(context).RequireUnsaved("");
            Call(context,"Bind",new object());Assert.ThrowsException<InvalidOperationException>(()=>Guard(context).RequireUnsaved(""));
        }
        [TestMethod] public void OpeningOrClearingRevokesFreshPartPermission()
        {
            var context=Context();Call(context,"BindCreatedPart",new object());
            var opened=new object();Call(context,"StageOpen",opened,new object(),true);
            Assert.ThrowsException<InvalidOperationException>(()=>Guard(context).RequireUnsaved(""));
            Call(context,"BindPendingOpen",opened,(Guid?)null);
            Assert.ThrowsException<InvalidOperationException>(()=>Guard(context).RequireUnsaved(""));
            Call(context,"BindCreatedPart",new object());Call(context,"Clear");
            Assert.ThrowsException<InvalidOperationException>(()=>Guard(context).RequireUnsaved(""));
        }
        [TestMethod] public void AnotherExecutionCannotInheritNewPartPermission()
        {
            var context=Context();var execution=Guid.NewGuid();Call(context,"BeginExecution",(Guid?)execution);
            Call(context,"BindCreatedPart",new object());Call(context,"BeginExecution",(Guid?)execution);Guard(context).RequireUnsaved("");
            Call(context,"BeginExecution",(Guid?)Guid.NewGuid());Assert.ThrowsException<InvalidOperationException>(()=>Guard(context).RequireUnsaved(""));
        }
    }
}
