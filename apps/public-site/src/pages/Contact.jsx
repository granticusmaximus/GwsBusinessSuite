import LayoutRenderer from '../cms/LayoutRenderer';
import { loadLayout } from '../cms/loadLayout';

const layout = loadLayout('Contact');

export default function Contact() {
  if (layout) {
    return <LayoutRenderer layout={layout} />;
  }

  return (
    <main className="contact-page">
      <h1>Say Hello</h1>
      <div className="accent-line" />
      <p>
        Have a project idea, want to work together, or just want to say hello?
        Fill out the form and I'll get back to you.
      </p>
      <form className="contact-form" onSubmit={(e) => e.preventDefault()}>
        <label>
          Name
          <input type="text" name="name" placeholder="Your name" />
        </label>
        <label>
          Email
          <input type="email" name="email" placeholder="your@email.com" />
        </label>
        <label>
          Message
          <textarea name="message" rows={6} placeholder="What's on your mind?" />
        </label>
        <div>
          <button type="submit" className="btn btn-primary">Send Message</button>
        </div>
      </form>
    </main>
  );
}
