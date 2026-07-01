import { useEffect, useState } from 'react';
import { useParams, Link } from 'react-router-dom';
import { API_BASE_URL } from '../apiBase';
import CmsBlockRenderer from '../cms/CmsBlockRenderer';

const CMS_SITE_SLUG = import.meta.env.VITE_CMS_SITE_SLUG || '';

export default function CmsPage() {
  const { pageSlug } = useParams();
  const [page, setPage] = useState(null);
  const [loading, setLoading] = useState(true);
  const [notFound, setNotFound] = useState(false);

  useEffect(() => {
    if (!CMS_SITE_SLUG) {
      setNotFound(true);
      setLoading(false);
      return;
    }

    // eslint-disable-next-line react-hooks/set-state-in-effect
    setLoading(true);
    setNotFound(false);

    fetch(`${API_BASE_URL}/api/cms/${CMS_SITE_SLUG}/pages/${pageSlug}`)
      .then(r => {
        if (!r.ok) throw new Error('not_found');
        return r.json();
      })
      .then(data => {
        setPage(data);
        document.title = data.metaTitle || data.title;
        const metaDesc = document.querySelector('meta[name="description"]');
        if (metaDesc && data.metaDescription) metaDesc.setAttribute('content', data.metaDescription);
      })
      .catch(() => setNotFound(true))
      .finally(() => setLoading(false));
  }, [pageSlug]);

  if (loading) return <div className="page-loading">Loading…</div>;

  if (notFound) {
    return (
      <main className="page-not-found">
        <h1>404</h1>
        <p>Page not found.</p>
        <Link to="/">← Back to Home</Link>
      </main>
    );
  }

  return (
    <>
      {(page.siteCustomCss || page.pageCustomCss) && (
        <style>{[page.siteCustomCss, page.pageCustomCss].filter(Boolean).join('\n')}</style>
      )}
      <CmsBlockRenderer
        blocks={page.blocks || []}
        siteSlug={CMS_SITE_SLUG}
        pageSlug={pageSlug}
      />
    </>
  );
}
