import React from 'react';

/**
 * Renders a GWS layout JSON (PageLayout) as React components.
 *
 * Usage in any page:
 *   import LayoutRenderer from '../cms/LayoutRenderer';
 *   import layout from './data/Home.layout.json';
 *   ...
 *   <LayoutRenderer layout={layout} />
 */
export default function LayoutRenderer({ layout }) {
  if (!layout?.sections?.length) return null;
  return (
    <div className="gws-layout">
      {layout.sections.map(section => (
        <section
          key={section.id}
          className={[
            'gws-section',
            bgClass(section.background),
            padClass(section.padding),
          ].filter(Boolean).join(' ')}
        >
          <div className={colsClass(section.columnLayout)}>
            {section.columns?.map(col => (
              <div key={col.id} className="gws-column">
                {col.widgets?.map(widget => (
                  <Widget key={widget.id} widget={widget} />
                ))}
              </div>
            ))}
          </div>
        </section>
      ))}
    </div>
  );
}

function Widget({ widget }) {
  const { widgetType, props = {} } = widget;

  switch (widgetType) {
    case 'hero':
      return (
        <div className={`gws-hero gws-align-${props.align || 'left'}`}>
          <h1 className="gws-hero-headline">{props.headline}</h1>
          {props.subline && <p className="gws-hero-subline">{props.subline}</p>}
          <div className="gws-hero-actions">
            {props.cta1Label && (
              <a href={props.cta1Href || '#'} className="btn btn-primary">{props.cta1Label}</a>
            )}
            {props.cta2Label && (
              <a href={props.cta2Href || '#'} className="btn btn-ghost">{props.cta2Label}</a>
            )}
          </div>
        </div>
      );

    case 'heading': {
      const Tag = props.level || 'h2';
      return (
        <Tag className={`gws-heading gws-align-${props.align || 'left'}`}>
          {props.text}
        </Tag>
      );
    }

    case 'paragraph':
      return (
        <p className={`gws-paragraph gws-align-${props.align || 'left'}`}>
          {props.text}
        </p>
      );

    case 'button':
      return (
        <div className={`gws-button-wrap gws-align-${props.align || 'left'}`}>
          <a
            href={props.href || '#'}
            className={`btn btn-${props.variant || 'primary'}`}
            target={props.openInNewTab === 'true' ? '_blank' : undefined}
            rel={props.openInNewTab === 'true' ? 'noopener noreferrer' : undefined}
          >
            {props.label}
          </a>
        </div>
      );

    case 'image':
      return props.src ? (
        <div className={`gws-image gws-image-${props.width || 'full'}`}>
          <img src={props.src} alt={props.alt || ''} />
          {props.caption && <p className="gws-image-caption">{props.caption}</p>}
        </div>
      ) : null;

    case 'card':
      return (
        <div className="gws-card">
          {props.imageSrc && <img src={props.imageSrc} alt="" className="gws-card-img" />}
          <div className="gws-card-body">
            <h3 className="gws-card-title">{props.title}</h3>
            <p className="gws-card-text">{props.body}</p>
            {props.link && (
              <a href={props.link} className="btn btn-sm btn-outline-primary">Read more</a>
            )}
          </div>
        </div>
      );

    case 'spacer':
      return <div className="gws-spacer" style={{ height: `${props.height || 48}px` }} />;

    case 'divider':
      return <hr className={`gws-divider gws-divider-${props.style || 'solid'}`} />;

    case 'html':
      // eslint-disable-next-line react/no-danger
      return <div className="gws-html" dangerouslySetInnerHTML={{ __html: props.content || '' }} />;

    default:
      return null;
  }
}

// ── Helpers ──────────────────────────────────────────────────────────────────

function bgClass(bg) {
  return { light: 'gws-bg-light', dark: 'gws-bg-dark', accent: 'gws-bg-accent' }[bg] || '';
}

function padClass(pad) {
  return {
    none: 'gws-pad-none',
    sm:   'gws-pad-sm',
    lg:   'gws-pad-lg',
    xl:   'gws-pad-xl',
  }[pad] || 'gws-pad-md';
}

function colsClass(layout) {
  return {
    'half-half':             'gws-columns gws-cols-2',
    'one-third-two-thirds':  'gws-columns gws-cols-1-2',
    'two-thirds-one-third':  'gws-columns gws-cols-2-1',
    'thirds':                'gws-columns gws-cols-3',
  }[layout] || 'gws-columns gws-cols-1';
}
