// publish.mjs — Build and publish PoeMapViewer as a single-file exe.
// Usage:  node publish.mjs

import { execSync } from 'child_process';
import { dirname } from 'path';
import { fileURLToPath } from 'url';

const root = dirname(fileURLToPath(import.meta.url));
const outDir = 'publish';

console.log('Publishing PoeMapViewer single-file exe...\n');

try {
  execSync(
    `dotnet publish src/POE2Radar.Overlay/POE2Radar.Overlay.csproj ` +
    `-c Release -r win-x64 --self-contained true ` +
    `-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ` +
    `-o ${outDir}`,
    { cwd: root, stdio: 'inherit' }
  );
  console.log(`\nPublished to: ${outDir}/PoeMapViewer.exe`);
} catch (e) {
  console.error('\nPublish failed.');
  process.exit(1);
}
