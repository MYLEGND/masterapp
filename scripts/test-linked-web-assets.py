#!/usr/bin/env python3
"""Verify canonical linked web assets through real SDK build and publish manifests."""
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from xml.sax.saxutils import escape


class LinkedWebAssetParityTests(unittest.TestCase):
    def test_original_sources_resolve_at_public_urls_and_publish_unchanged(self):
        props = Path(__file__).resolve().parents[1] / 'Directory.Build.props'
        with tempfile.TemporaryDirectory(prefix='legend-linked-assets-') as temporary:
            root = Path(temporary).resolve()
            app, shared = root / 'app', root / 'shared'
            (app / 'wwwroot').mkdir(parents=True)
            shared.mkdir()
            (root / 'Directory.Build.props').write_text(
                '<Project><Import Project="' + escape(str(props)) + '" /></Project>')
            sources = {
                'design/remapped.css': shared / 'canonical.css',
                'js/nested/remapped.js': shared / 'canonical.js',
                'explicit/kept.css': shared / 'explicit.css',
                'ordinary.css': app / 'wwwroot/ordinary.css',
            }
            for index, source in enumerate(sources.values()):
                source.write_text('/* canonical fixture ' + str(index) + ' */')
            (shared / 'not-web.txt').write_text('not a web asset')
            (app / 'Program.cs').write_text(
                'var builder = WebApplication.CreateBuilder(args); '
                'var app = builder.Build(); app.UseStaticFiles(); app.Run();')
            (app / 'Fixture.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk.Web">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
<ItemGroup>
<Content Include="../shared/canonical.css" Link="wwwroot/design/remapped.css" CopyToPublishDirectory="PreserveNewest" />
<Content Include="../shared/canonical.js" Link="wwwroot\\js\\nested\\remapped.js" CopyToPublishDirectory="PreserveNewest" />
<Content Include="../shared/explicit.css" Link="wwwroot/explicit/kept.css" ContentRoot="''' + escape(str(root)) + '''/" CopyToPublishDirectory="PreserveNewest" />
<Content Include="../shared/not-web.txt" Link="configuration/not-web.txt" />
</ItemGroup></Project>''')
            result = subprocess.run([
                'dotnet', 'publish', str(app / 'Fixture.csproj'), '--nologo',
                '-m:1', '-nr:false', '-p:UseSharedCompilation=false',
                '-p:MasterAppArtifactsRoot=' + str(root / 'artifacts'),
                '-o', str(root / 'published')], capture_output=True, text=True, timeout=180)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            manifest = json.loads(next((root / 'artifacts').rglob(
                'Fixture.staticwebassets.runtime.json')).read_text())
            for url, source in sources.items():
                with self.subTest(url=url):
                    node = manifest['Root']
                    for part in url.split('/'):
                        node = node['Children'][part]
                    asset = node['Asset']
                    physical = Path(manifest['ContentRoots'][asset['ContentRootIndex']]) / asset['SubPath']
                    self.assertEqual(source, physical.resolve())
                    self.assertEqual(source.read_bytes(), (root / 'published/wwwroot' / url).read_bytes())
                    if url == 'explicit/kept.css':
                        self.assertEqual(root, Path(manifest['ContentRoots'][asset['ContentRootIndex']]))
            self.assertNotIn('configuration', manifest['Root']['Children'])


if __name__ == '__main__':
    unittest.main()
