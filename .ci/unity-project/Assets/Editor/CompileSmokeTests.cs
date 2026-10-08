using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor.Compilation;

namespace WDT.CI
{
    public class CompileSmokeTests
    {
        [Test]
        public void RequiredTTTEditorAssembliesWereCompiled()
        {
            var assemblies = CompilationPipeline.GetAssemblies()
                .Select(assembly => assembly.name)
                .ToArray();

            foreach (var required in new[]
            {
                "net.rs64.tex-trans-tool.runtime",
                "net.rs64.tex-trans-tool.editor",
                "net.rs64.tex-trans-tool.inspector",
                "net.rs64.tex-trans-tool.ndmf"
            })
            {
                Assert.That(assemblies, Does.Contain(required),
                    "Required assembly is missing from the CI project: " + required);
            }
        }
    }
}
