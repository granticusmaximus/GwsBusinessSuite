import { Link } from 'react-router-dom';
import { resolveBackendUrl } from '../apiBase';

export default function ArticleCard({ article }) {
  const date = new Date(article.publishedAt).toLocaleDateString('en-US', {
    month: 'short', year: 'numeric',
  });

  const desc = article.metaDescription
    ? article.metaDescription.length > 125
      ? article.metaDescription.slice(0, 125) + '…'
      : article.metaDescription
    : null;

  return (
    <Link to={`/blog/${article.slug}`} className="article-card">
      {article.hasHeroImage && article.heroImageUrl ? (
        <img
          src={resolveBackendUrl(article.heroImageUrl)}
          alt={article.title}
          className="article-card-img"
          loading="lazy"
        />
      ) : (
        <div className="article-card-img-placeholder">No image</div>
      )}
      <div className="article-card-body">
        <div className="article-card-title">{article.title}</div>
        {desc && <p className="article-card-desc">{desc}</p>}
        <div className="article-card-meta">
          {article.estimatedReadingTime && (
            <span>{article.estimatedReadingTime} read</span>
          )}
          {article.primaryKeyword && (
            <span className="article-tag">{article.primaryKeyword}</span>
          )}
          <span>{date}</span>
        </div>
      </div>
    </Link>
  );
}
