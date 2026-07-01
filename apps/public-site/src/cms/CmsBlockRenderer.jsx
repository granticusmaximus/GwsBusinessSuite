import { useEffect, useState } from 'react';
import { API_BASE_URL } from '../apiBase';

/**
 * Renders a Canvas page layout (Section -> Column -> Widget JSON, the shape
 * saved by the Studio / CmsBuilderEditor.razor) as React components. This is
 * the live, runtime-fetched rendering path — no build/deploy needed for a
 * page's content to change, unlike the retired file-based layout builder
 * this replaces.
 *
 * In edit mode (?cms_edit=1), widgets are clickable: clicking one sends a
 * postMessage to the parent window (the Blazor Studio) so the inspector can
 * display its properties. The parent can push prop updates back via:
 *   { type: 'cms:update-props', widgetId, props }
 * Not currently wired up by the Studio (its stage renders server-side HTML
 * instead), but kept so a future live click-to-select editor can reuse it.
 *
 * Usage:
 *   import CmsBlockRenderer from '../cms/CmsBlockRenderer';
 *   <CmsBlockRenderer layout={page.blocks} siteSlug={...} pageSlug={...} />
 */

function isEditMode() {
  return new URLSearchParams(window.location.search).get('cms_edit') === '1';
}

export default function CmsBlockRenderer({ layout, siteSlug = '', pageSlug = '' }) {
  const editMode = isEditMode();
  const [liveProps, setLiveProps] = useState({});
  const [selectedId, setSelectedId] = useState(null);

  useEffect(() => {
    if (!editMode) return;

    function onMessage(e) {
      const d = e.data;
      if (!d || typeof d !== 'object') return;
      if (d.type === 'cms:update-props') {
        setLiveProps(prev => ({ ...prev, [d.widgetId]: d.props }));
      }
    }

    window.addEventListener('message', onMessage);
    window.parent.postMessage({ type: 'cms:ready' }, '*');
    return () => window.removeEventListener('message', onMessage);
  }, [editMode]);

  if (!layout?.sections?.length) return null;

  function selectWidget(sectionId, widgetId) {
    if (!editMode) return;
    setSelectedId(widgetId);
    window.parent.postMessage({ type: 'cms:select', sectionId, widgetId }, '*');
  }

  function selectSection(sectionId) {
    if (!editMode) return;
    setSelectedId(sectionId);
    window.parent.postMessage({ type: 'cms:select-section', sectionId }, '*');
  }

  function resolveProps(widget) {
    const override = liveProps[widget.id];
    return override ? { ...widget.props, ...override } : (widget.props || {});
  }

  return (
    <div className="gws-layout">
      {layout.sections.map(section => (
        <section
          key={section.id}
          className={[
            'gws-section',
            bgClass(section.background),
            padClass(section.padding),
            editMode ? 'gws-editable-section' : '',
            editMode && selectedId === section.id ? 'gws-selected' : '',
          ].filter(Boolean).join(' ')}
          onClick={editMode ? e => { e.stopPropagation(); selectSection(section.id); } : undefined}
        >
          {editMode && (
            <div className="gws-section-label">{section.label || 'Section'}</div>
          )}
          <div className={colsClass(section.columnLayout)}>
            {section.columns?.map(col => (
              <div key={col.id} className="gws-column">
                {col.widgets?.map(widget => (
                  <div
                    key={widget.id}
                    className={[
                      'gws-widget-wrap',
                      editMode ? 'gws-editable' : '',
                      editMode && selectedId === widget.id ? 'gws-selected' : '',
                    ].filter(Boolean).join(' ')}
                    onClick={editMode ? e => { e.stopPropagation(); selectWidget(section.id, widget.id); } : undefined}
                  >
                    {editMode && (
                      <span className="gws-widget-badge">{widget.widgetType}</span>
                    )}
                    <Widget widget={widget} resolvedProps={resolveProps(widget)} siteSlug={siteSlug} pageSlug={pageSlug} />
                  </div>
                ))}
              </div>
            ))}
          </div>
        </section>
      ))}

      {editMode && (
        <style>{editModeStyles}</style>
      )}
    </div>
  );
}

// ── Widget renderer ────────────────────────────────────────────────────────

function Widget({ widget, resolvedProps: p = {}, siteSlug, pageSlug }) {
  switch (widget.widgetType) {
    case 'hero':
      return (
        <div className={`gws-hero gws-align-${p.align || 'left'}`}>
          <h1 className="gws-hero-headline">{p.headline}</h1>
          {p.subline && <p className="gws-hero-subline">{p.subline}</p>}
          <div className="gws-hero-actions">
            {p.cta1Label && (
              <a href={p.cta1Href || '#'} className="btn btn-primary">{p.cta1Label}</a>
            )}
            {p.cta2Label && (
              <a href={p.cta2Href || '#'} className="btn btn-ghost">{p.cta2Label}</a>
            )}
          </div>
        </div>
      );

    case 'heading': {
      const Tag = p.level || 'h2';
      return (
        <Tag className={`gws-heading gws-align-${p.align || 'left'}`}>
          {p.text}
        </Tag>
      );
    }

    case 'paragraph':
      return (
        <p className={`gws-paragraph gws-align-${p.align || 'left'}`}>
          {p.text}
        </p>
      );

    case 'button':
      return (
        <div className={`gws-button-wrap gws-align-${p.align || 'left'}`}>
          <a
            href={p.href || '#'}
            className={`btn btn-${p.variant || 'primary'}`}
            target={p.openInNewTab === 'true' ? '_blank' : undefined}
            rel={p.openInNewTab === 'true' ? 'noopener noreferrer' : undefined}
          >
            {p.label}
          </a>
        </div>
      );

    case 'image':
      return p.src ? (
        <div className={`gws-image gws-image-${p.width || 'full'}`}>
          <img src={p.src} alt={p.alt || ''} />
          {p.caption && <p className="gws-image-caption">{p.caption}</p>}
        </div>
      ) : null;

    case 'card':
      return (
        <div className="gws-card">
          {p.imageSrc && <img src={p.imageSrc} alt="" className="gws-card-img" />}
          <div className="gws-card-body">
            <h3 className="gws-card-title">{p.title}</h3>
            <p className="gws-card-text">{p.body}</p>
            {p.link && (
              <a href={p.link} className="btn btn-sm btn-outline-primary">Read more</a>
            )}
          </div>
        </div>
      );

    case 'spacer':
      return <div className="gws-spacer" style={{ height: `${p.height || 48}px` }} />;

    case 'divider':
      return <hr className={`gws-divider gws-divider-${p.style || 'solid'}`} />;

    case 'html':
      return <div className="gws-html" dangerouslySetInnerHTML={{ __html: p.content || '' }} />;

    case 'form':
      return <FormWidget props={p} siteSlug={siteSlug} pageSlug={pageSlug} />;

    default:
      return null;
  }
}

// ── Form widget: admin-defined arbitrary fields ────────────────────────────

function FormWidget({ props: p, siteSlug, pageSlug }) {
  let fields;
  try {
    fields = JSON.parse(p.fieldsJson || '[]');
  } catch {
    fields = [];
  }

  return (
    <form
      className="gws-form"
      method="post"
      action={`${API_BASE_URL}/cms/${siteSlug}/${pageSlug}/submit`}
    >
      {fields.map(field => (
        <label key={field.key} className="gws-form-field">
          <span className="gws-form-label">
            {field.label}
            {field.required && <span className="gws-form-required">*</span>}
          </span>
          <FormField field={field} />
        </label>
      ))}
      {/* Honeypot: hidden from real visitors, bots fill every field they find. */}
      <input type="text" name="company" className="gws-form-honeypot" tabIndex={-1} autoComplete="off" />
      <button type="submit" className="btn btn-primary gws-form-submit">
        {p.submitLabel || 'Submit'}
      </button>
    </form>
  );
}

function FormField({ field }) {
  const common = { name: field.key, required: !!field.required };

  switch (field.type) {
    case 'textarea':
      return <textarea rows={4} {...common} />;
    case 'select': {
      let options;
      try {
        options = JSON.parse(field.optionsJson || '[]');
      } catch {
        options = [];
      }
      return (
        <select {...common}>
          <option value="">Select…</option>
          {options.map(opt => <option key={opt} value={opt}>{opt}</option>)}
        </select>
      );
    }
    case 'checkbox':
      return <input type="checkbox" {...common} />;
    case 'tel':
      return <input type="tel" {...common} />;
    case 'email':
      return <input type="email" {...common} />;
    case 'text':
    default:
      return <input type="text" {...common} />;
  }
}

// ── Helpers ────────────────────────────────────────────────────────────────

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

// ── Edit mode overlay styles (injected only in ?cms_edit=1) ───────────────

const editModeStyles = `
  .gws-editable { position: relative; cursor: pointer; transition: outline 80ms; }
  .gws-editable:hover { outline: 2px dashed rgba(99,102,241,.6); outline-offset: 2px; }
  .gws-editable.gws-selected { outline: 2px solid #6366f1 !important; outline-offset: 2px; }
  .gws-widget-badge {
    position: absolute; top: 0; left: 0;
    background: #6366f1; color: #fff;
    font: 600 10px/1 sans-serif; letter-spacing: .04em;
    padding: 2px 6px; border-radius: 0 0 4px 0;
    text-transform: uppercase; opacity: 0; pointer-events: none;
    transition: opacity 120ms; z-index: 9999;
  }
  .gws-editable:hover .gws-widget-badge,
  .gws-editable.gws-selected .gws-widget-badge { opacity: 1; }
  .gws-editable-section { position: relative; }
  .gws-editable-section:hover { outline: 1px dashed rgba(99,102,241,.3); }
  .gws-editable-section.gws-selected { outline: 1px solid rgba(99,102,241,.5); }
  .gws-section-label {
    position: absolute; top: 4px; right: 8px;
    background: rgba(99,102,241,.15); color: #818cf8;
    font: 600 10px/1 sans-serif; padding: 3px 8px;
    border-radius: 4px; pointer-events: none; z-index: 9999;
  }
`;
