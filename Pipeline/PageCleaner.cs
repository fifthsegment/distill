namespace Distill.Pipeline;

/// <summary>
/// General-purpose JS scripts injected into a rendered page before HTML extraction.
/// These are site-agnostic — they work on any page by targeting semantic HTML patterns.
/// </summary>
public static class PageCleaner
{
    public static readonly IReadOnlyList<string> AllScripts =
    [
        AnnotateImageBadges,
        StripChromeElements,
        StripHiddenElements,
        StripEmptyContainers,
    ];

    /// <summary>
    /// Finds images that convey meaning but have no alt text, and injects visible text labels.
    /// Targets: badge images, icon-only buttons, logo links.
    /// Uses aria-label, title, nearby text, parent context, and known badge patterns.
    /// </summary>
    public const string AnnotateImageBadges = """
        (() => {
          const imgs = document.querySelectorAll('img:not([alt]), img[alt=""]');
          for (const img of imgs) {
            // Skip large content images (product photos, hero images)
            const w = img.naturalWidth || img.width || parseInt(img.getAttribute('width')) || 0;
            const h = img.naturalHeight || img.height || parseInt(img.getAttribute('height')) || 0;
            if (w > 400 && h > 400) continue;

            // Try to derive meaning from the image or its context
            let label = '';

            // 1. aria-label on the image or parent
            label = label || img.getAttribute('aria-label')
                          || img.closest('[aria-label]')?.getAttribute('aria-label');

            // 2. title attribute
            label = label || img.getAttribute('title')
                          || img.parentElement?.getAttribute('title');

            // 3. Sibling text in the same container (e.g. icon + label)
            if (!label) {
              const parent = img.parentElement;
              if (parent && parent.children.length <= 3) {
                const siblingText = Array.from(parent.childNodes)
                  .filter(n => n !== img && n.nodeType === 3)
                  .map(n => n.textContent.trim())
                  .filter(Boolean)
                  .join(' ');
                if (siblingText && siblingText.length < 50) label = siblingText;
              }
            }

            // 4. src filename as last resort for small badge-like images
            if (!label && w > 0 && w < 250 && h > 0 && h < 100) {
              const src = img.getAttribute('src') || '';
              const filename = src.split('/').pop()?.split('?')[0]?.split('.')[0] || '';
              // Only use filename if it looks meaningful (not a hash)
              if (filename && /^[a-zA-Z]/.test(filename) && filename.length < 30 && !/^[a-f0-9]{16,}$/i.test(filename)) {
                label = filename.replace(/[-_]/g, ' ');
              }
            }

            if (label) {
              img.setAttribute('alt', label);
            }
          }
        })()
        """;

    /// <summary>
    /// Removes site chrome: navigation, headers, footers, sidebars, cookie banners,
    /// modals, overlays, ad containers. Uses semantic HTML + common class/role patterns.
    /// </summary>
    public const string StripChromeElements = """
        (() => {
          const bodyText = document.body.innerText.length;
          // Guard: if removing an element would kill >40% of visible text, skip it.
          // This prevents destroying the page on sites like AliExpress where the
          // entire app is inside a <header> or <nav>.
          const safeRemove = el => {
            if (!el.isConnected) return;
            const elText = (el.innerText || '').length;
            if (elText > bodyText * 0.4) return;
            el.remove();
          };

          // Semantic elements that are almost always chrome
          const semanticSelectors = [
            'nav', 'header', 'footer',
            '[role="navigation"]', '[role="banner"]', '[role="contentinfo"]',
            '[role="dialog"]', '[role="alertdialog"]',
          ];

          // Class/id patterns for common chrome elements
          const chromePatterns = [
            'cookie', 'consent', 'gdpr', 'privacy-banner',
            'notification-bar', 'subscribe-popup', 'newsletter-popup',
            'overlay', 'modal', 'popup',
            'sticky-header', 'site-header', 'site-footer',
            'breadcrumb',
            'social-share', 'share-buttons',
          ];

          for (const sel of semanticSelectors) {
            document.querySelectorAll(sel).forEach(safeRemove);
          }

          const allElements = document.querySelectorAll('*');
          for (const el of allElements) {
            if (!el.isConnected) continue;
            const id = (el.id || '').toLowerCase();
            const cls = (el.className?.toString?.() || '').toLowerCase();
            const combined = id + ' ' + cls;

            for (const pattern of chromePatterns) {
              if (combined.includes(pattern)) {
                safeRemove(el);
                break;
              }
            }
          }

          // Remove fixed/sticky positioned elements (floating bars, chat widgets)
          const computed = document.querySelectorAll('*');
          for (const el of computed) {
            if (!el.isConnected) continue;
            const style = window.getComputedStyle(el);
            if ((style.position === 'fixed' || style.position === 'sticky') &&
                el.tagName !== 'HTML' && el.tagName !== 'BODY') {
              safeRemove(el);
            }
          }
        })()
        """;

    /// <summary>
    /// Removes elements that are visually hidden but would pollute extracted text.
    /// </summary>
    public const string StripHiddenElements = """
        (() => {
          document.querySelectorAll('[aria-hidden="true"], [hidden], [style*="display:none"], [style*="display: none"]').forEach(el => {
            // Keep if it has meaningful alt text on images inside
            if (!el.querySelector('img[alt]:not([alt=""])')) {
              el.remove();
            }
          });
        })()
        """;

    /// <summary>
    /// Removes containers that are now empty after cleanup (just whitespace/empty divs).
    /// </summary>
    public const string StripEmptyContainers = """
        (() => {
          // Multiple passes to handle nested empty containers
          for (let pass = 0; pass < 3; pass++) {
            document.querySelectorAll('div, section, aside, span').forEach(el => {
              if (!el.isConnected) return;
              if (el.children.length === 0 && !el.textContent.trim()) {
                el.remove();
              }
            });
          }
        })()
        """;
}
