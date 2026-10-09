// MSS-068: minimal ESM loader so Node's test runner can import Vite
// components that reference stylesheets for side effects.
// Styles are presentation-only; tests assert sporting/render behavior and
// never depend on CSS content. Returning an empty module keeps the
// render-level regression tests offline, deterministic and fast.
export async function resolve(specifier, context, next) {
  if (specifier.endsWith('.css')) {
    return {
      url: new URL(specifier, context.parentURL).href,
      shortCircuit: true,
    };
  }
  return next(specifier, context);
}

export async function load(url, context, next) {
  if (url.endsWith('.css')) {
    return {
      format: 'module',
      source: 'export default {};',
      shortCircuit: true,
    };
  }
  return next(url, context);
}
