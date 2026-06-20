import { useEffect, useState, useMemo } from 'react';
import ArticleCard from '../components/ArticleCard';
import { API_BASE_URL } from '../apiBase';

const PAGE_SIZE_OPTIONS = [10, 25, 50];

export default function BlogList() {
  const [articles, setArticles]       = useState([]);
  const [loading, setLoading]         = useState(true);
  const [activeKeyword, setKeyword]   = useState(null);
  const [page, setPage]               = useState(1);
  const [pageSize, setPageSize]       = useState(10);

  useEffect(() => {
    fetch(`${API_BASE_URL}/api/blog`)
      .then(r => r.ok ? r.json() : [])
      .then(data => {
        // sort by publishedAt descending (API should already do this, but be safe)
        const sorted = [...data].sort((a, b) => {
          const da = a.publishedAt ? new Date(a.publishedAt) : new Date(0);
          const db = b.publishedAt ? new Date(b.publishedAt) : new Date(0);
          return db - da;
        });
        setArticles(sorted);
      })
      .catch(() => {})
      .finally(() => setLoading(false));
  }, []);

  // Unique, non-empty keywords sorted alphabetically
  const keywords = useMemo(() => {
    const seen = new Set();
    articles.forEach(a => {
      if (a.primaryKeyword) seen.add(a.primaryKeyword);
    });
    return [...seen].sort((a, b) => a.localeCompare(b));
  }, [articles]);

  // Filtered list based on active keyword
  const filtered = useMemo(() => {
    if (!activeKeyword) return articles;
    return articles.filter(a => a.primaryKeyword === activeKeyword);
  }, [articles, activeKeyword]);

  // Reset to page 1 whenever filter or page size changes
  const totalPages = Math.max(1, Math.ceil(filtered.length / pageSize));
  const safePage   = Math.min(page, totalPages);
  const paginated  = filtered.slice((safePage - 1) * pageSize, safePage * pageSize);

  function selectKeyword(kw) {
    setKeyword(prev => prev === kw ? null : kw);
    setPage(1);
  }

  function changePageSize(n) {
    setPageSize(n);
    setPage(1);
  }

  return (
    <main className="blog-page">
      <header className="blog-page-header">
        <h1>Blog</h1>
        <div className="blog-page-accent" />
        <p>Thoughts on software, building products, and the web.</p>
      </header>

      {loading ? (
        <p className="page-loading">Loading articles…</p>
      ) : articles.length === 0 ? (
        <p style={{ color: 'var(--text-2)' }}>No articles yet. Check back soon.</p>
      ) : (
        <>
          {/* Keyword cloud */}
          {keywords.length > 0 && (
            <section className="keyword-cloud" aria-label="Filter by keyword">
              <span className="keyword-cloud-label">Filter:</span>
              {keywords.map(kw => (
                <button
                  key={kw}
                  className={`keyword-pill${activeKeyword === kw ? ' active' : ''}`}
                  onClick={() => selectKeyword(kw)}
                  aria-pressed={activeKeyword === kw}
                >
                  {kw}
                </button>
              ))}
              {activeKeyword && (
                <button className="keyword-clear" onClick={() => { setKeyword(null); setPage(1); }}>
                  Clear
                </button>
              )}
            </section>
          )}

          {/* Result count + page-size picker */}
          <div className="blog-controls">
            <span className="blog-result-count">
              {filtered.length === articles.length
                ? `${articles.length} article${articles.length !== 1 ? 's' : ''}`
                : `${filtered.length} of ${articles.length} articles`}
            </span>
            <div className="blog-pagesize">
              <span>Show:</span>
              {PAGE_SIZE_OPTIONS.map(n => (
                <button
                  key={n}
                  className={`pagesize-btn${pageSize === n ? ' active' : ''}`}
                  onClick={() => changePageSize(n)}
                  aria-pressed={pageSize === n}
                >
                  {n}
                </button>
              ))}
            </div>
          </div>

          <div className="blog-grid">
            {paginated.map(a => (
              <ArticleCard key={a.slug} article={a} />
            ))}
          </div>

          {/* Pagination */}
          {totalPages > 1 && (
            <nav className="blog-pagination" aria-label="Page navigation">
              <button
                className="pagination-btn"
                onClick={() => setPage(p => Math.max(1, p - 1))}
                disabled={safePage === 1}
                aria-label="Previous page"
              >
                ← Prev
              </button>

              {Array.from({ length: totalPages }, (_, i) => i + 1).map(n => (
                <button
                  key={n}
                  className={`pagination-btn${safePage === n ? ' active' : ''}`}
                  onClick={() => setPage(n)}
                  aria-current={safePage === n ? 'page' : undefined}
                >
                  {n}
                </button>
              ))}

              <button
                className="pagination-btn"
                onClick={() => setPage(p => Math.min(totalPages, p + 1))}
                disabled={safePage === totalPages}
                aria-label="Next page"
              >
                Next →
              </button>
            </nav>
          )}
        </>
      )}
    </main>
  );
}
