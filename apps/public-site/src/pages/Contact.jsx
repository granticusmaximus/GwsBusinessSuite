export default function Contact() {
  return (
    <main className="page-contact">
      <section className="content-section">
        <h1>Contact</h1>
        <p>Want to work together or just say hello? Reach out below.</p>
        <form className="contact-form" onSubmit={(e) => e.preventDefault()}>
          <label>Name<input type="text" name="name" placeholder="Your name" /></label>
          <label>Email<input type="email" name="email" placeholder="your@email.com" /></label>
          <label>Message<textarea name="message" rows={5} placeholder="What is on your mind?" /></label>
          <button type="submit" className="btn btn-primary">Send Message</button>
        </form>
        <p><a href="/" className="nav-back">← Back to Home</a></p>
      </section>
    </main>
  );
}
