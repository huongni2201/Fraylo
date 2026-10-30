import fs from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { transformWithOxc } from "vite";

export async function resolve(specifier, context, nextResolve) {
  if (specifier.endsWith(".css")) {
    return {
      url: "data:text/javascript,export default {};",
      format: "module",
      shortCircuit: true,
    };
  }
  if (specifier.startsWith("@/")) {
    const srcDir = fileURLToPath(new URL("../", import.meta.url));
    const subPath = specifier.slice(2);
    const resolvedPath = path.join(srcDir, subPath);
    for (const ext of ["", ".ts", ".tsx", "/index.ts", "/index.tsx"]) {
      const candidate = resolvedPath + ext;
      if (fs.existsSync(candidate) && fs.statSync(candidate).isFile()) {
        return {
          url: pathToFileURL(candidate).href,
          format: "module",
          shortCircuit: true,
        };
      }
    }
  }
  try {
    return await nextResolve(specifier, context);
  } catch (err) {
    if (specifier.startsWith(".")) {
      for (const ext of [".ts", ".tsx", "/index.ts", "/index.tsx"]) {
        try {
          return await nextResolve(specifier + ext, context);
        } catch {}
      }
    }
    throw err;
  }
}

export async function load(url, context, nextLoad) {
  if (url.endsWith(".tsx") || (url.endsWith(".ts") && !url.includes("node_modules"))) {
    const filePath = fileURLToPath(url);
    const raw = fs.readFileSync(filePath, "utf-8");
    const transformed = await transformWithOxc(raw, filePath, {
      jsx: { runtime: "classic" },
    });
    let code = transformed.code;
    const hasReactIdentifier = /import\s+(?:\*\s+as\s+React|React[\s,])/.test(code);
    if (url.endsWith(".tsx") && !hasReactIdentifier) {
      code = 'import React from "react";\n' + code;
    }
    return {
      format: "module",
      shortCircuit: true,
      source: code,
    };
  }
  return nextLoad(url, context);
}
