import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('impact', Path(__file__).with_name('diagnostic-project-impact.py'))
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)

class ImpactTests(unittest.TestCase):
    def test_future_dotnet_project_and_dependency_closure(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for name, content in {'Core/Core.csproj': '<Project/>', 'NewApp/NewApp.csproj': '<Project><ItemGroup><ProjectReference Include="../Core/Core.csproj"/></ItemGroup></Project>', 'Other/Other.csproj': '<Project/>'}.items():
                p = root / name
                p.parent.mkdir(exist_ok=True)
                p.write_text(content)
            graph = m.discover(root, [str(p.relative_to(root)) for p in root.rglob('*.csproj')])
            affected, reasons = m.impact(graph, ['Core/Failure.cs'])
            self.assertEqual(affected, ['Core/Core.csproj', 'NewApp/NewApp.csproj'])
            self.assertEqual(reasons, [])
    def test_unowned_shared_input_broadens(self):
        graph = {'a': {'root':'a','dependencies':[],'externalInputs':[],'uncertainDependencies':False}, 'b': {'root':'b','dependencies':[],'externalInputs':[],'uncertainDependencies':False}}
        affected, reasons = m.impact(graph, ['Directory.Build.props'])
        self.assertEqual(affected, ['a', 'b'])
        self.assertTrue(reasons)
    def test_deleted_project_is_not_silently_ignored(self):
        graph = {'a': {'root':'a','dependencies':[],'externalInputs':[],'uncertainDependencies':False}}
        self.assertEqual(m.impact(graph, ['old/old.csproj'])[0], ['a'])
    def test_no_change_means_no_targets(self):
        self.assertEqual(m.impact({}, []), ([], []))

if __name__ == '__main__':
    unittest.main()
