using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.SolidWorksBridge;
using SolidWorksCadAgent.SolidWorksBridge.Session;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
#endif

namespace SolidWorksCadAgent.IntegrationTests
{
    [TestClass]
    [TestCategory("SolidWorksIntegration")]
    public class DocumentTargetTests
    {
        [TestMethod]
        public async Task SwitchedActiveDocument_RejectsMutationSaveAndCloseOfOtherPart()
        {
#if SOLIDWORKS_INTEROP
            using (var session = new SolidWorksSession())
            using (var original = new SolidWorksBridgeFacade(session))
            using (var other = new SolidWorksBridgeFacade(session))
            {
                Assert.IsTrue((await session.AttachAsync(CancellationToken.None)).IsConnected, "Start SOLIDWORKS before the document-switch test.");
                string originalTitle = null, otherTitle = null;
                try
                {
                    var created = await Execute(original, CadCommandNames.NewPart, new { });
                    Assert.IsTrue(created.Success, created.Error?.Message);
                    originalTitle = (string)created.Data["documentTitle"];
                    var second = await Execute(other, CadCommandNames.NewPart, new { });
                    Assert.IsTrue(second.Success, second.Error?.Message);
                    otherTitle = (string)second.Data["documentTitle"];
                    foreach (var command in new[]
                    {
                        new CadCommandEnvelope { Command = CadCommandNames.CreateSketch, Parameters = JObject.FromObject(new { plane = "Top Plane" }) },
                        new CadCommandEnvelope { Command = CadCommandNames.Rebuild, Parameters = new JObject() },
                        new CadCommandEnvelope { Command = CadCommandNames.SavePart, Parameters = JObject.FromObject(new { path = "integration\\RejectedSwitch-" + Guid.NewGuid().ToString("N") + ".sldprt" }) },
                        new CadCommandEnvelope { Command = CadCommandNames.CloseDocument, Parameters = new JObject() }
                    })
                    {
                        var result = await original.ExecuteAsync(command, CancellationToken.None);
                        Assert.IsFalse(result.Success, command.Command + " must reject the unrelated active part.");
                        Assert.AreEqual("DOCUMENT_TARGET_CHANGED", result.Error?.Code);
                    }
                    Assert.AreEqual(otherTitle, await session.InvokeWithApplicationAsync(app => ((ModelDoc2)((SldWorks)app).ActiveDoc).GetTitle(), CancellationToken.None));
                    Assert.IsTrue((await Execute(other, CadCommandNames.CloseDocument, new { })).Success);
                    otherTitle = null;
                    var activated = await session.InvokeWithApplicationAsync(app =>
                    {
                        var errors = 0;
                        return ((SldWorks)app).ActivateDoc3(originalTitle, false, 0, ref errors) != null;
                    }, CancellationToken.None);
                    Assert.IsTrue(activated);
                    Assert.IsTrue((await Execute(original, CadCommandNames.Rebuild, new { })).Success, "Original binding must remain usable after restoring its document.");
                }
                finally
                {
                    // Only close the two documents created by this test, never the user's active document.
                    await session.InvokeWithApplicationAsync(app =>
                    {
                        if (otherTitle != null) ((SldWorks)app).CloseDoc(otherTitle);
                        if (originalTitle != null) ((SldWorks)app).CloseDoc(originalTitle);
                        return true;
                    }, CancellationToken.None);
                }
            }
#else
            Assert.Inconclusive("Installed SOLIDWORKS interop is required.");
#endif
        }
        private static Task<CadCommandResult> Execute(SolidWorksBridgeFacade bridge, string name, object parameters) =>
            bridge.ExecuteAsync(new CadCommandEnvelope { Command = name, Parameters = JObject.FromObject(parameters) }, CancellationToken.None);
    }
}
