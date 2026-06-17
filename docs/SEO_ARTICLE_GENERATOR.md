# SEO Article Generator

The SEO Article Generator creates technical C#/.NET article drafts using your local Ollama model. It is designed as an approval workflow, not an auto-publishing machine. Generated articles should be reviewed before publishing.

## Workflow

1. Enter topic, target audience, primary keyword, and secondary keywords.
2. The app sends a structured system prompt and user prompt to Ollama.
3. Ollama returns Markdown containing title, metadata, outline, article body, code samples, checklist, and references to verify.
4. The draft is saved with `PendingReview` status.
5. You can approve, reject, or request modifications.

## Ollama Settings

Update `src/$WEB_PROJECT_NAME/appsettings.json`:

```json
{
  "ExternalServices": {
    "OllamaBaseUrl": "http://localhost:11434",
    "OllamaModel": "llama3.1"
  }
}
```

## Security Note

Do not expose Ollama directly to the public internet. If the dashboard runs on a DigitalOcean droplet and Ollama runs on your Mac, use a secured local bridge or private tunnel with API-key protection.

## Editorial Standard

The agent prompt instructs the model to behave like a senior technical writer with journalism and computer science expertise, specializing in C#, ASP.NET Core, Blazor, and enterprise software.

## Article Image Branding

All future AI-generated article images should include the official `grantwatson.dev` favicon or logo mark as a subtle branded element.

See `docs/ARTICLE_IMAGE_BRANDING.md` for the exact rule, asset paths, and reusable Claude prompt.
