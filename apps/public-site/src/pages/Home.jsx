import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import ArticleCard from '../components/ArticleCard';

export default function Home() {
  const [recentArticles, setRecentArticles] = useState([]);

  useEffect(() => {
    fetch('/api/blog')
      .then(r => r.ok ? r.json() : [])
      .then(data => setRecentArticles(data.slice(0, 3)))
      .catch(() => {});
  }, []);

  return (
    <>
      <section className="hero">
        <div className="hero-inner">
          <p className="hero-greeting">Hey, I'm</p>
          <h1 className="hero-name">
            Grant<br />Watson.
          </h1>
          <div className="hero-divider" />
          <p className="hero-role">Developer · Builder · Creator</p>
          <p className="hero-bio">
            I build products that live on the web — from bespoke CMS tools to
            AI-powered content pipelines. Always shipping, always learning.
          </p>
          <div className="hero-actions">
            <Link to="/blog" className="btn btn-primary">Read the Blog</Link>
            <a
              href="https://github.com/granticusmaximus"
              target="_blank"
              rel="noopener noreferrer"
              className="btn btn-ghost"
            >
              GitHub ↗
            </a>
            <Link to="/contact" className="btn btn-ghost">Get In Touch</Link>
          </div>
        </div>
      </section>

      {recentArticles.length > 0 && (
        <section className="home-blog">
          <div className="home-blog-header">
            <h2>From the blog</h2>
            <Link to="/blog" className="view-all">View all →</Link>
          </div>
          <div className="home-blog-grid">
            {recentArticles.map(a => (
              <ArticleCard key={a.id} article={a} />
            ))}
          </div>
        </section>
      )}
    </>
  );
}
