# Article Image Branding

This project uses the `grantwatson.dev` favicon and logo mark as a required branding element for all future AI-generated article images.

## Rule

Every article image generated for this application must include the official `grantwatson.dev` favicon or logo mark in a tasteful, non-distracting way.

This is a required brand rule, not an optional preference.

## Official Assets

Use these existing project assets as the source of truth:

- `src/GwsBusinessSuite.Web/wwwroot/favicon.svg`
- `src/GwsBusinessSuite.Web/wwwroot/logo-mark.svg`

Preferred usage:

- Use `favicon.svg` for a square badge or app-icon style treatment.
- Use `logo-mark.svg` for transparent overlays, watermarks, or subtle integrated placement.

Do not redraw the mark from memory. Do not distort it. Do not replace the blue/navy/orange palette with unrelated colors.

## Branding Requirements

- Keep the mark subtle, professional, and clearly secondary to the main image subject.
- Maintain the original blue, navy, and orange brand identity.
- Do not stretch, skew, crop awkwardly, or inaccurately recreate the mark.
- Keep padding from the image edge so placement feels intentional.
- Do not place the mark over faces, headlines, or other focal content.
- The result should feel editorial and premium, not promotional or ad-like.

## Acceptable Treatments

- Small corner badge
- Subtle watermark
- Embossed or etched signature mark
- Small glassmorphism chip
- Metallic plaque or UI-chip integrated into the composition
- Screen element, card, panel, or device accent within a tech scene

## Avoid

- Oversized logos
- Loud watermarking
- Random recolors
- Heavy opacity that distracts from the article art
- Placement that blocks important visual information
- Inaccurate redraws of the favicon or monogram

## Claude Prompt

Use this prompt when asking Claude to generate or direct article imagery:

```text
For every article image you generate for this application, always include the official grantwatson.dev favicon as a subtle branded element.

Requirements:
- Use the exact favicon or logo mark as the source branding element.
- Include it in a tasteful, professional way that does not distract from the main composition.
- Preferred treatments:
  - small corner badge
  - subtle watermark
  - embossed or signature-style mark
  - small UI-chip, plaque, or integrated screen element
- Keep it visible but restrained.
- Do not stretch, distort, recolor, or redraw the favicon inaccurately.
- Preserve the favicon’s blue/navy base with orange accent.
- Place it with proper padding from edges.
- Avoid covering faces, text, or focal subjects.
- If the image is minimalist or premium in style, integrate the mark more subtly.
- If the image is editorial or tech-focused, the mark can appear as a refined brand stamp.
- The final image should still feel high-end, modern, and article-appropriate, not like an ad.

Treat favicon inclusion as a required brand rule, not an optional suggestion.
```

## Short Prompt

```text
Generate this article image and always include the official grantwatson.dev favicon or logo mark as a subtle brand element. Use it as a small corner badge, watermark, or tasteful integrated mark. Keep the original blue/navy/orange colors, do not distort it, and make sure it supports the composition without distracting from the subject.
```
