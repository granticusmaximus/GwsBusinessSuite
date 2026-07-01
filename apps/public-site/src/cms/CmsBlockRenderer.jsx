import { Marked } from 'marked';

const md = new Marked({ gfm: true, breaks: true, async: false });

function safeHtml(text) {
  if (!text) return '';
  return md.parse('').constructor === String ? text : text;
}

// Renders a single block object. CSS class names intentionally match what
// CmsBlockHtmlRenderer.cs emits on the server so the admin preview and the live
// React page look identical without keeping two separate stylesheets in sync.
function Block({ block }) {
  const { type, ...p } = block;

  switch (type) {
    case 'hero':
      return (
        <div className="cms-hero">
          <h1>{p.title}</h1>
          {p.subtitle && <p>{p.subtitle}</p>}
          {p.primaryCta && (
            <a className="cms-button" href={p.primaryCtaHref || '#'}>{p.primaryCta}</a>
          )}
        </div>
      );

    case 'proof-grid':
      return (
        <>
          <h2>{p.title}</h2>
          <div className="cms-grid">
            {(p.items || []).map((item, i) => (
              <div key={i} className="cms-grid-item">{item}</div>
            ))}
          </div>
        </>
      );

    case 'feature-stack':
      return (
        <>
          <h2>{p.title}</h2>
          <ul className="cms-list">
            {(p.items || []).map((item, i) => <li key={i}>{item}</li>)}
          </ul>
        </>
      );

    case 'cta':
      return (
        <div className="cms-callout">
          <h2>{p.title}</h2>
          {p.button && (
            <a className="cms-button" href={p.buttonHref || '#'}>{p.button}</a>
          )}
        </div>
      );

    case 'article-header':
      return (
        <>
          <h1>{p.title}</h1>
          {p.subtitle && <p>{p.subtitle}</p>}
        </>
      );

    case 'toc':
      return <h2>{p.title || 'In this article'}</h2>;

    case 'rich-content':
      return (
        <div
          className="cms-rich-content"
          // eslint-disable-next-line react/no-danger
          dangerouslySetInnerHTML={{ __html: md.parse(p.body || '') }}
        />
      );

    case 'author-box':
      return (
        <div className="cms-author">
          <div className="cms-author-name">{p.name}</div>
          <div className="cms-author-role">{p.role}</div>
        </div>
      );

    case 'newsletter-cta':
      return (
        <div className="cms-callout">
          <h2>{p.title}</h2>
          {p.button && (
            <a className="cms-button" href={p.buttonHref || '#'}>{p.button}</a>
          )}
        </div>
      );

    case 'countdown':
      return (
        <div className="cms-countdown">
          <h2>{p.title || 'Countdown'}</h2>
          <div className="cms-countdown-days">{p.days ?? 7}</div>
          <div className="cms-countdown-caption">days remaining</div>
        </div>
      );

    case 'pricing-table':
      return (
        <>
          <h2>{p.title || 'Pricing'}</h2>
          <div className="cms-grid">
            {(p.plans || []).map((plan, i) => (
              <div key={i} className="cms-grid-item"><strong>{plan}</strong></div>
            ))}
          </div>
        </>
      );

    case 'faq':
      return <h2>{p.title || 'Frequently asked questions'}</h2>;

    case 'service-list':
      return (
        <>
          <h2>{p.title || 'Services'}</h2>
          <div className="cms-grid">
            {(p.items || []).map((item, i) => (
              <div key={i} className="cms-grid-item">{item}</div>
            ))}
          </div>
        </>
      );

    case 'testimonials':
      return <h2>{p.title || 'Client results'}</h2>;

    case 'contact-form':
      // The form action points at the .NET submit endpoint.
      return (
        <div className="cms-callout">
          <h2>{p.title || 'Get in touch'}</h2>
          <form
            className="cms-form-grid"
            method="post"
            action={`${window.__CMS_API_BASE__ || ''}/cms/${p._siteSlug || ''}/${p._pageSlug || ''}/submit`}
          >
            <input type="text" name="name" placeholder="Name" required maxLength={200} />
            <input type="email" name="email" placeholder="Email" required maxLength={320} />
            <textarea name="message" rows={3} placeholder="Project details" required maxLength={5000} />
            <input type="text" name="company" className="cms-form-honeypot" tabIndex={-1} autoComplete="off" />
            <button type="submit" className="cms-button">Send</button>
          </form>
        </div>
      );

    case 'image':
      return p.src ? (
        <img className="cms-image" src={p.src} alt={p.alt || ''} />
      ) : null;

    default:
      return null;
  }
}

export default function CmsBlockRenderer({ blocks = [], siteSlug = '', pageSlug = '' }) {
  if (!blocks.length) return null;

  return (
    <>
      {blocks.map((block, i) => (
        <section key={i} className="cms-block">
          <Block block={{ ...block, _siteSlug: siteSlug, _pageSlug: pageSlug }} />
        </section>
      ))}
    </>
  );
}
