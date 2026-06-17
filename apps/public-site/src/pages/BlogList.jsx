import { useEffect, useState } from 'react';
import ArticleCard from '../components/ArticleCard';

export default function BlogList() {
  const [articles, setArticles] = useState([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    fetch('/api/blog')
      .then(r => r.ok ? r.json() : [])
      .then(setArticles)
      .catch(() => {})
      .finally(() => setLoading(false));
  }, []);

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
        <div className="blog-grid">
          {articles.map(a => (
            <ArticleCard key={a.id} article={a} />
          ))}
        </div>
      )}
    </main>
  );
}
