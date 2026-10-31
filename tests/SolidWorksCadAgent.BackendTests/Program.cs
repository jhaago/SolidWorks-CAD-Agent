using System.Xml.Linq;
using SolidWorksCadAgent.Core.Remote;
using SolidWorksCadAgent.Core.Backends;
using SolidWorksCadEngine.Adapter;

var root=Path.Combine(Path.GetTempPath(),"cad-backend-boundary-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
var path=Path.Combine(root,"cad.lock");int passed=0;
void Check(bool test,string name){if(!test)throw new Exception(name);Console.WriteLine("PASS "+name);passed++;}
using(var backend=new FileCadExecutionBoundary(path,"test",Path.Combine(root,"journal")))
    Check(CadNativeControlLease.TryAcquire(path)==null,"legacy cannot enter upstream job");
using(var legacy=CadNativeControlLease.TryAcquire(path)) {
    bool blocked=false;try{using var backend=new FileCadExecutionBoundary(path,"test",Path.Combine(root,"journal"));}catch(IOException){blocked=true;}
    Check(blocked,"upstream cannot enter legacy job");
}
File.WriteAllText(path+".recovery-required","unknown native outcome");
using(var legacy=CadNativeControlLease.TryAcquire(path))Check(legacy==null,"legacy respects recovery quarantine");
File.Delete(path+".recovery-required");using(var legacy=CadNativeControlLease.TryAcquire(path))Check(legacy!=null,"operator reconciliation releases quarantine");
bool disabled=false;try{await UpstreamBackendJob.StartAsync(new EngineOptions(),Guid.NewGuid(),Path.Combine(root,"journal"),path);}catch(InvalidOperationException){disabled=true;}
Check(disabled,"local facade requires explicit opt-in");
// Metadata policy must allow current references while continuing to reject missing/mismatched pairs.
var repo=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../.."));
var xml=XDocument.Load(Path.Combine(repo,"build/SolidWorks2020Interop.targets"));
Check(!xml.ToString().Contains("StartsWith('28.')"),"compile guard has no version-28 requirement");
Check(xml.Descendants("Error").Count()==2,"missing and mismatched references remain guarded");
Console.WriteLine($"Application backend tests: {passed}/{passed} passed");
