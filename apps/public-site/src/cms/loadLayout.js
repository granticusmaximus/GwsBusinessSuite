// CmsBuilderEditor (Blazor admin) saves edited pages to ./data/{PageKey}.layout.json via
// PageLayoutService. Pages that have never been opened/saved in the builder have no file
// here, so callers must treat a null return as "render the hand-written JSX instead."
const layoutModules = import.meta.glob('../pages/data/*.layout.json', { eager: true });

export function loadLayout(pageKey) {
  const entry = Object.entries(layoutModules).find(([path]) =>
    path.endsWith(`/${pageKey}.layout.json`)
  );
  if (!entry) return null;
  return entry[1].default ?? entry[1];
}
