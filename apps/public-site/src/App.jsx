import { BrowserRouter, Routes, Route, Link } from 'react-router-dom';
import './App.css';
import Home from './pages/Home';
import About from './pages/About';
import Contact from './pages/Contact';
import BlogList from './pages/BlogList';
import BlogPost from './pages/BlogPost';

const ADMIN_URL = import.meta.env.DEV
  ? 'http://localhost:5050/admin'
  : 'https://admin.gwsapp.net/admin';

function Navbar() {
  return (
    <nav className="site-nav">
      <Link to="/" className="site-logo">
        <img src="/logo-mark.svg" alt="" />
        <span className="site-logo-wordmark">
          <span>grantwatson</span>
          <span className="site-logo-domain">.dev</span>
        </span>
      </Link>
      <div className="nav-links">
        <Link to="/about">About</Link>
        <Link to="/blog">Blog</Link>
        <Link to="/contact">Contact</Link>
        {import.meta.env.DEV && <a href={ADMIN_URL} className="nav-admin">Admin ↗</a>}
      </div>
    </nav>
  );
}

function Footer() {
  return (
    <footer className="site-footer">
      <p>© {new Date().getFullYear()} Grant Watson</p>
      <a href={ADMIN_URL} className="footer-admin-link">admin</a>
    </footer>
  );
}

function NotFound() {
  return (
    <main className="page-not-found">
      <h1>404</h1>
      <p>Page not found.</p>
      <Link to="/">← Back to Home</Link>
    </main>
  );
}

export default function App() {
  return (
    <BrowserRouter>
      <Navbar />
      <Routes>
        <Route path="/" element={<Home />} />
        <Route path="/about" element={<About />} />
        <Route path="/contact" element={<Contact />} />
        <Route path="/blog" element={<BlogList />} />
        <Route path="/blog/:slug" element={<BlogPost />} />
        <Route path="*" element={<NotFound />} />
      </Routes>
      <Footer />
    </BrowserRouter>
  );
}
