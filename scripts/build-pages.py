#!/usr/bin/env python3
"""Assembles what GitHub Pages serves: the marketing site (site/) at the root and the built guide (build/guide) under
guide/. Run after `mkdocs build`. Writes build/pages.

The site's placeholders are filled from environment variables (repository variables in .github/workflows/docs.yml).
Anything not set is left out rather than shipped as a dead link:

  SITE_URL          https://finepointcgi.github.io/Workbench (no trailing slash)
  DEMO_URL          the public demo; without it the demo buttons open the guide instead
  SALES_EMAIL       for "Contact" and "Ask for a Business key"; without it they open a GitHub issue
  FORM_ENDPOINT     where the contact form posts (e.g. Formspree); without it the form is hidden
  ANALYTICS_DOMAIN  the Plausible site domain; without it the analytics script is removed
"""
import os
import re
import shutil
from pathlib import Path

root = Path(__file__).resolve().parent.parent
site, guide, out = root / 'site', root / 'build' / 'guide', root / 'build' / 'pages'

site_url = os.environ.get('SITE_URL', 'https://finepointcgi.github.io/Workbench').rstrip('/')
demo = os.environ.get('DEMO_URL', '').strip()
email = os.environ.get('SALES_EMAIL', '').strip()
form = os.environ.get('FORM_ENDPOINT', '').strip()
analytics = os.environ.get('ANALYTICS_DOMAIN', '').strip()
issues = 'https://github.com/finepointcgi/Workbench/issues/new'

if not (guide / 'index.html').exists():
    raise SystemExit('build/guide is missing: run mkdocs build first')

shutil.rmtree(out, ignore_errors=True)
shutil.copytree(site, out)
shutil.copytree(guide, out / 'guide')

no_demo_text = {'Try the demo': 'See the guide', 'Try the live demo': 'See every screen', 'Demo': 'Guide'}


def demo_link(m):
    if demo:
        return m.group(0).replace('https://REPLACE-WITH-DEMO-URL', demo)
    return f'<a{m.group(1)}href="guide/">{no_demo_text.get(m.group(2), m.group(2))}</a>'


def guide_page(m):
    """A docs/ file on GitHub becomes its page in the guide, when the guide publishes it."""
    page = re.sub(r'(^|/)(README|index)$', r'\1', m.group(1)).strip('/')
    return f'guide/{page}/' if (guide / page / 'index.html').exists() else m.group(0)


for path in out.glob('*.*'):
    if path.suffix not in ('.html', '.xml', '.txt'):
        continue
    s = path.read_text()
    s = s.replace('https://REPLACE-WITH-DOMAIN', site_url)
    # The pages sit at the top level, but the site is served from /Workbench/, so root-relative links must go.
    s = re.sub(r'(href|src)="/(?!/)', r'\1="./', s)
    s = s.replace('href="https://finepointcgi.github.io/Workbench/guide/"', 'href="guide/"')
    s = re.sub(r'<a([^>]*?)href="https://REPLACE-WITH-DEMO-URL">([^<]*)</a>', demo_link, s)
    s = re.sub(r'https://github\.com/finepointcgi/workbench/blob/master/docs/([\w/.-]+)\.md', guide_page, s, flags=re.I)
    s = s.replace('mailto:REPLACE-WITH-SALES-EMAIL', f'mailto:{email}' if email else issues)
    if form:
        s = s.replace('https://REPLACE-WITH-FORM-ENDPOINT', form)
    else:
        s = re.sub(r'\s*<section id="contact">.*?</section>', '', s, flags=re.S)
        s = s.replace('<a href="#contact">Contact</a>', '')
    if analytics:
        s = s.replace('data-domain="REPLACE-WITH-DOMAIN"', f'data-domain="{analytics}"')
    else:
        s = re.sub(r'<script defer data-domain="[^"]*" src="https://plausible.io/js/script.js"></script>\s*', '', s)
    if 'REPLACE-WITH' in s:
        raise SystemExit(f'{path.name} still has a placeholder')
    path.write_text(s)

# The guide used to be served from the root: send its old addresses to their new place.
for page in guide.rglob('index.html'):
    rel = page.relative_to(guide)
    if rel.parent == Path('.') or (out / rel).exists():
        continue
    target = '../' * len(rel.parent.parts) + 'guide/' + rel.parent.as_posix() + '/'
    (out / rel).parent.mkdir(parents=True, exist_ok=True)
    (out / rel).write_text(f'<!doctype html><meta charset="utf-8"><title>Moved</title>'
                           f'<link rel="canonical" href="{site_url}/guide/{rel.parent.as_posix()}/">'
                           f'<meta http-equiv="refresh" content="0; url={target}">'
                           f'<a href="{target}">This page moved.</a>\n')

shutil.copy(guide / '404.html', out / '404.html')
print(f'build/pages: site at /, guide at /guide/ (demo: {"set" if demo else "guide"}, email: {"set" if email else "issues"}, '
      f'form: {"set" if form else "hidden"}, analytics: {"on" if analytics else "off"})')
