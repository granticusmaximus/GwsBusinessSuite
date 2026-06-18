import { useEffect, useState } from 'react';
import { useParams, Link } from 'react-router-dom';
import { Marked } from 'marked';

const md = new Marked({ gfm: true, breaks: true, async: false });

function renderMarkdown(text) {
  if (!text) return '';
  try {
    return md.parse(text);
  } catch {
    return `<pre>${text}</pre>`;
  }
}

export default function BlogPost() {
  const { slug } = useParams();
  const [article, setArticle] = useState(null);
  const [loading, setLoading]  = useState(true);
  const [error, setError]      = useState(null);

  useEffect(() => {
    setLoading(true);
    setError(null);
    fetch(`/api/blog/${slug}`)
      .then(r => {
        if (!r.ok) throw new Error(r.status === 404 ? 'not_found' : 'error');
        return r.json();
      })
      .then(setArticle)
      .catch(e => setError(e.message))
      .finally(() => setLoading(false));
  }, [slug]);

  if (loading) return <div className="page-loading">Loading…</div>;

  if (error === 'not_found') {
    return (
      <main className="page-not-found">
        <h1>404</h1>
        <p>Article not found.</p>
        <Link to="/blog">← Back to Blog</Link>
      </main>
    );
  }

  if (error) return <div className="page-loading">Could not load article.</div>;

  if (!article) return null;

  const publishedDate = new Date(article.publishedAt).toLocaleDateString('en-US', {
    year: 'numeric', month: 'long', day: 'numeric',
  });

  return (
    <article>
      {article.hasHeroImage && article.heroImageUrl && (
        <>
          <div className="blog-post-hero-wrap">
            <img
              src={article.heroImageUrl}
              alt={article.heroImageAltText || article.title}
            />
          </div>
          {article.heroImageCaption && (
            <p className="blog-post-hero-caption">{article.heroImageCaption}</p>
          )}
        </>
      )}

      <div className="blog-post-content">
        <div className="blog-post-meta-row">
          <span>{publishedDate}</span>
          {article.estimatedReadingTime && (
            <span>· {article.estimatedReadingTime} read</span>
          )}
          {article.primaryKeyword && (
            <span className="article-tag">{article.primaryKeyword}</span>
          )}
        </div>

        <h1 className="blog-post-title">{article.title}</h1>

        {article.metaDescription && (
          <p className="blog-post-lead">{article.metaDescription}</p>
        )}

        <p className="blog-post-author">By {article.author}</p>

        {article.articleMarkdown ? (
          <div
            className="blog-post-body"
            dangerouslySetInnerHTML={{ __html: renderMarkdown(article.articleMarkdown) }}
          />
        ) : (
          <p className="blog-post-body" style={{ color: 'var(--text-3)', fontStyle: 'italic' }}>
            No content available for this article.
          </p>
        )}

        <footer className="blog-post-footer">
          <Link to="/blog">← All articles</Link>
        </footer>
      </div>
    </article>
  );
}
