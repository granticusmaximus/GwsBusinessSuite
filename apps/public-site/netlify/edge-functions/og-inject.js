/**
 * Netlify Edge Function — OG tag injection for /blog/:slug
 *
 * Social crawlers can't run JavaScript, so we intercept blog page requests
 * at the edge, fetch the article data from the backend, and bake Open Graph /
 * Twitter Card meta tags into the HTML before it reaches the crawler.
 */
export default async (request, context) => {
  const url = new URL(request.url);
  const slug = url.pathname.replace(/^\/blog\//, '').replace(/\/$/, '');

  if (!slug) return context.next();

  const backendUrl = Netlify.env.get('BACKEND_URL') || 'http://localhost:5000';

  let article;
  try {
    const apiRes = await fetch(`${backendUrl}/api/blog/${slug}`);
    if (!apiRes.ok) return context.next();
    article = await apiRes.json();
  } catch {
    return context.next();
  }

  const response = await context.next();
  let html = await response.text();

  const title       = article.title || '';
  const description = article.metaDescription || '';
  const imageUrl    = article.hasHeroImage ? `${backendUrl}/og-image/${slug}` : null;
  const published   = article.publishedAt || '';

  const ogBlock = [
    `<title>${esc(title)}</title>`,
    `<link rel="canonical" href="${url.href}" />`,
    description ? `<meta name="description" content="${esc(description)}" />` : '',
    `<meta name="author" content="Grant Watson" />`,
    `<meta property="og:type" content="article" />`,
    `<meta property="og:site_name" content="Grant Watson" />`,
    `<meta property="og:url" content="${url.href}" />`,
    `<meta property="og:title" content="${esc(title)}" />`,
    description ? `<meta property="og:description" content="${esc(description)}" />` : '',
    imageUrl ? `<meta property="og:image" content="${imageUrl}" />` : '',
    published ? `<meta property="article:published_time" content="${published}" />` : '',
    `<meta name="twitter:card" content="${imageUrl ? 'summary_large_image' : 'summary'}" />`,
    `<meta name="twitter:title" content="${esc(title)}" />`,
    description ? `<meta name="twitter:description" content="${esc(description)}" />` : '',
    imageUrl ? `<meta name="twitter:image" content="${imageUrl}" />` : '',
  ].filter(Boolean).map(tag => `  ${tag}`).join('\n');

  html = html.replace(/<title>[^<]*<\/title>/, '');
  html = html.replace('</head>', `${ogBlock}\n</head>`);

  return new Response(html, {
    headers: { 'content-type': 'text/html; charset=utf-8' },
  });
};

function esc(str) {
  return String(str)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

export const config = { path: '/blog/*' };
