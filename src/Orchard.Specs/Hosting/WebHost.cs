using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Web;
using System.Web.Hosting;
using Orchard.Specs.Util;
using Path = Bleroy.FluentPath.Path;

namespace Orchard.Specs.Hosting {
    public class WebHost {
        private readonly Path _orchardTemp;
        private WebHostAgent _webHostAgent;
        private Path _tempSite;
        private Path _orchardWebPath;
        private Path _codeGenDir;

        public WebHost(Path orchardTemp) {
            _orchardTemp = orchardTemp;
        }

        public void Initialize(string templateName, string virtualDirectory) {
            var baseDir = Path.Get(AppDomain.CurrentDomain.BaseDirectory);

            _tempSite = Path.Get(_orchardTemp).Combine(System.IO.Path.GetRandomFileName());
            try { _tempSite.Delete(); }
            catch { }

            // Trying the two known relative paths to the Orchard.Web directory.
            // The second one is for the target "spec" in orchard.proj.
            _orchardWebPath = baseDir.Up(3).Combine("Orchard.Web");
            if (!_orchardWebPath.Exists) {
                _orchardWebPath = baseDir.Parent.Combine("stage");
            }

            baseDir.Combine("Hosting").Combine(templateName)
                .DeepCopy(_tempSite);

            _orchardWebPath.Combine("bin")
                .ShallowCopy("*.dll", _tempSite.Combine("bin"))
                .ShallowCopy("*.pdb", _tempSite.Combine("bin"));

            // Copy SqlCe binaries
            if (_orchardWebPath.Combine("bin").Combine("x86").IsDirectory) {
                _orchardWebPath.Combine("bin").Combine("x86")
                    .ShallowCopy("*.dll", _tempSite.Combine("bin").Combine("x86"))
                    .ShallowCopy("*.pdb", _tempSite.Combine("bin").Combine("x86"));
            }

            if (_orchardWebPath.Combine("bin").Combine("amd64").IsDirectory) {
                _orchardWebPath.Combine("bin").Combine("amd64")
                    .ShallowCopy("*.dll", _tempSite.Combine("bin").Combine("amd64"))
                    .ShallowCopy("*.pdb", _tempSite.Combine("bin").Combine("amd64"));
            }

            baseDir
                .ShallowCopy("*.dll", _tempSite.Combine("bin"))
                .ShallowCopy("*.exe", _tempSite.Combine("bin"))
                .ShallowCopy("*.pdb", _tempSite.Combine("bin"));

            HostName = "localhost";
            PhysicalDirectory = _tempSite;
            VirtualDirectory = virtualDirectory;

            _webHostAgent = (WebHostAgent)ApplicationHost.CreateApplicationHost(typeof(WebHostAgent), VirtualDirectory, PhysicalDirectory);

            var shuttle = new Shuttle();
            Execute(() => { shuttle.CodeGenDir = HttpRuntime.CodegenDir; });

            // ASP.NET folder seems to be always nested into an empty directory
            _codeGenDir = shuttle.CodeGenDir;
            _codeGenDir = _codeGenDir.Parent;
        }

        [Serializable]
        class Shuttle {
            public string CodeGenDir;
        }

        public void Dispose() {
            if (_webHostAgent != null) {
                _webHostAgent.Shutdown();
                _webHostAgent = null;
            }
            Clean();
        }

        public void Clean() {
            // Try to delete temporary files for up to ~1.2 seconds.
            for (int i = 0; i < 4; i++) {
                Trace.WriteLine("Waiting 300msec before trying to delete temporary files");
                Thread.Sleep(300);

                if (TryDeleteTempFiles()) {
                    Trace.WriteLine("Successfully deleted all temporary files");
                    break;
                }
            }
        }

        private bool TryDeleteTempFiles() {
            var result = true;
            if (_codeGenDir != null && _codeGenDir.Exists) {
                Trace.WriteLine(string.Format("Trying to delete temporary files at '{0}", _codeGenDir));
                try {
                    _codeGenDir.Delete(true); // <- clean as much as possible
                }
                catch(Exception e) {
                    Trace.WriteLine(string.Format("failure: '{0}", e));
                    result = false;
                }
            }

            if (_tempSite != null && _tempSite.Exists)
            try {
                Trace.WriteLine(string.Format("Trying to delete temporary files at '{0}", _tempSite));
                _tempSite.Delete(true); // <- progressively clean as much as possible
            }
            catch (Exception e) {
                Trace.WriteLine(string.Format("failure: '{0}", e));
                result = false;
            }

            return result;
        }

        public void CopyExtension(string extensionFolder, string extensionName, ExtensionDeploymentOptions deploymentOptions) {
            var sourceModule = _orchardWebPath.Combine(extensionFolder).Combine(extensionName);
            var targetModule = _tempSite.Combine(extensionFolder).Combine(extensionName);

            sourceModule.ShallowCopy("*.txt", targetModule);
            sourceModule.ShallowCopy("*.info", targetModule);

            if ((deploymentOptions & ExtensionDeploymentOptions.SourceCode) == ExtensionDeploymentOptions.SourceCode) {
                sourceModule.ShallowCopy("*.csproj", targetModule);
                sourceModule.DeepCopy("*.cs", targetModule);
            }

            if (sourceModule.Combine("bin").IsDirectory) {
                sourceModule.Combine("bin").ShallowCopy(path => IsExtensionBinaryFile(path, extensionName, deploymentOptions), targetModule.Combine("bin"));
            }

            if (sourceModule.Combine("Views").IsDirectory)
                sourceModule.Combine("Views").DeepCopy(targetModule.Combine("Views"));
        }

        private bool IsExtensionBinaryFile(Path path, string extensionName, ExtensionDeploymentOptions deploymentOptions) {
            bool isValidExtension =
                StringComparer.OrdinalIgnoreCase.Equals(path.Extension, ".exe") ||
                StringComparer.OrdinalIgnoreCase.Equals(path.Extension, ".dll") ||
                StringComparer.OrdinalIgnoreCase.Equals(path.Extension, ".pdb");

            if (!isValidExtension)
                return false;

            bool isExtensionAssembly = StringComparer.OrdinalIgnoreCase.Equals(path.FileNameWithoutExtension, extensionName);
            bool copyExtensionAssembly = (deploymentOptions & ExtensionDeploymentOptions.CompiledAssembly) == ExtensionDeploymentOptions.CompiledAssembly;
            if (isExtensionAssembly && !copyExtensionAssembly)
                return false;

            return true;
        }

        public string HostName { get; set; }
        public string PhysicalDirectory { get; private set; }
        public string VirtualDirectory { get; private set; }

        public string Cookies { get; set; }


        public void Execute(Action action) {
            var shuttleSend = new SerializableDelegate<Action>(action);
            var shuttleRecv = _webHostAgent.Execute(shuttleSend);
            CopyFields(shuttleRecv.Delegate.Target, shuttleSend.Delegate.Target);
        }

        private static void CopyFields<T>(T from, T to) where T : class {
            if (from == null || to == null)
                return;
            foreach (FieldInfo fieldInfo in from.GetType().GetFields()) {
                var value = fieldInfo.GetValue(from);
                fieldInfo.SetValue(to, value);
            }
        }
    }
}