// Manual asset maintenance, never part of build/publish/runtime. Unix.svg is the
// original, font-free source; the committed PNG/ICO need no rendering dependency.
// Requires an existing Node + sharp development environment. An existing sharp
// module can be selected with BABYAI_SHARP_MODULE (module name or absolute path).
//   node scripts/windows/generate-unix-icons.mjs         # regenerate PNG + ICO
//   node scripts/windows/generate-unix-icons.mjs --check # compare without writes
import { createRequire } from 'node:module';
import { readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const sharp = require(process.env.BABYAI_SHARP_MODULE || 'sharp');
const check = process.argv.length === 3 && process.argv[2] === '--check';
if (process.argv.length > (check ? 3 : 2)) throw new Error('Only --check is supported');
const assets = new URL('../../desktop/BabyAI.Desktop/Assets/', import.meta.url);
const svg = await readFile(new URL('Unix.svg', assets));
const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
const render = size => sharp(svg, { density: 384 }).resize(size, size, { kernel: 'lanczos3' });
const images = [];
for (const size of sizes) {
    if (size >= 128) {
        images.push(await render(size).png({ compressionLevel: 9 }).toBuffer());
        continue;
    }
    // Classic 32-bit DIB + AND mask for small Shell/tray frames; larger frames
    // use PNG. Retain per-pixel alpha and bottom-up BGRA ordering on Windows 10.
    const rgba = await render(size).ensureAlpha().raw().toBuffer();
    const maskStride = Math.ceil(size / 32) * 4;
    const dib = Buffer.alloc(40 + size * size * 4 + maskStride * size);
    dib.writeUInt32LE(40, 0);
    dib.writeInt32LE(size, 4);
    dib.writeInt32LE(size * 2, 8);
    dib.writeUInt16LE(1, 12);
    dib.writeUInt16LE(32, 14);
    dib.writeUInt32LE(size * size * 4, 20);
    for (let y = 0; y < size; y++) {
        for (let x = 0; x < size; x++) {
            const source = ((size - 1 - y) * size + x) * 4;
            const target = 40 + (y * size + x) * 4;
            dib[target] = rgba[source + 2];
            dib[target + 1] = rgba[source + 1];
            dib[target + 2] = rgba[source];
            dib[target + 3] = rgba[source + 3];
            if (rgba[source + 3] === 0)
                dib[40 + size * size * 4 + y * maskStride + Math.floor(x / 8)] |= 1 << (7 - x % 8);
        }
    }
    images.push(dib);
}
const header = Buffer.alloc(6 + sizes.length * 16);
header.writeUInt16LE(1, 2);
header.writeUInt16LE(sizes.length, 4);
let offset = header.length;
for (let i = 0; i < sizes.length; i++) {
    const entry = 6 + i * 16;
    header[entry] = header[entry + 1] = sizes[i] === 256 ? 0 : sizes[i];
    header.writeUInt16LE(1, entry + 4);
    header.writeUInt16LE(32, entry + 6);
    header.writeUInt32LE(images[i].length, entry + 8);
    header.writeUInt32LE(offset, entry + 12);
    offset += images[i].length;
}
const files = new Map([
    ['Unix.ico', Buffer.concat([header, ...images])],
    ['Unix.png', await render(512).png({ compressionLevel: 9 }).toBuffer()],
]);
for (const [name, bytes] of files) {
    const path = new URL(name, assets);
    if (check) {
        if (!bytes.equals(await readFile(path))) throw new Error(`${name} does not match Unix.svg; regenerate assets`);
    } else await writeFile(path, bytes);
    console.log(`${check ? 'Verified' : 'Wrote'} ${fileURLToPath(path)} (${bytes.length} bytes)`);
}
