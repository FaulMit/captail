const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

const revealItems = document.querySelectorAll('[data-reveal]');
if (reducedMotion || !('IntersectionObserver' in window)) {
  revealItems.forEach((item) => item.classList.add('is-visible'));
} else {
  const revealObserver = new IntersectionObserver((entries, observer) => {
    entries.forEach((entry) => {
      if (!entry.isIntersecting) return;
      entry.target.classList.add('is-visible');
      observer.unobserve(entry.target);
    });
  }, { threshold: 0.12, rootMargin: '0px 0px -5% 0px' });

  revealItems.forEach((item) => revealObserver.observe(item));
}

const progressBar = document.querySelector('.scroll-signal span');
let frameRequested = false;

function renderScrollEffects() {
  const scrollTop = window.scrollY || document.documentElement.scrollTop;
  const scrollRange = document.documentElement.scrollHeight - window.innerHeight;
  const progress = scrollRange > 0 ? Math.min(scrollTop / scrollRange, 1) : 0;

  if (progressBar) progressBar.style.transform = `scaleX(${progress})`;
  frameRequested = false;
}

function requestScrollFrame() {
  if (frameRequested) return;
  frameRequested = true;
  window.requestAnimationFrame(renderScrollEffects);
}

window.addEventListener('scroll', requestScrollFrame, { passive: true });
window.addEventListener('resize', requestScrollFrame, { passive: true });
renderScrollEffects();

document.querySelectorAll('.faq-list details').forEach((detail) => {
  detail.addEventListener('toggle', () => {
    if (!detail.open) return;
    document.querySelectorAll('.faq-list details[open]').forEach((other) => {
      if (other !== detail) other.open = false;
    });
  });
});

const downloadDialog = document.querySelector('[data-download-dialog]');
const downloadStatus = document.querySelector('[data-download-status]');
const setupDownload = document.querySelector('[data-download-setup]');
const portableDownload = document.querySelector('[data-download-portable]');
const releaseLink = document.querySelector('[data-download-release]');
let downloadsResolved = false;

function closeDownloadDialog() {
  if (!downloadDialog) return;
  if (typeof downloadDialog.close === 'function') downloadDialog.close();
  else downloadDialog.removeAttribute('open');
  document.body.classList.remove('has-download-dialog');
}

async function resolveLatestDownloads() {
  if (downloadsResolved) return;
  downloadsResolved = true;

  try {
    const response = await fetch('https://api.github.com/repos/FaulMit/captail/releases?per_page=10', {
      headers: { Accept: 'application/vnd.github+json' }
    });
    if (!response.ok) throw new Error(`GitHub API returned ${response.status}`);

    const releases = await response.json();
    const release = releases.find((item) => !item.draft);
    const setupAsset = release?.assets?.find((asset) => /Setup-win-x64\.exe$/i.test(asset.name));
    const portableAsset = release?.assets?.find((asset) => /Portable-win-x64\.zip$/i.test(asset.name));
    if (!release || !setupAsset || !portableAsset) throw new Error('Release assets are incomplete');

    setupDownload.href = setupAsset.browser_download_url;
    portableDownload.href = portableAsset.browser_download_url;
    releaseLink.href = release.html_url;
    const version = release.tag_name.toUpperCase();
    downloadStatus.textContent = `Latest GitHub preview · ${version}`;
    document.querySelectorAll('[data-current-version]').forEach((item) => {
      item.textContent = item.textContent === item.textContent.toUpperCase()
        ? version
        : release.tag_name;
    });
  } catch (error) {
    downloadStatus.textContent = 'Latest verified fallback · V0.1.6';
    console.warn('Could not resolve latest Captail release; using v0.1.6 links.', error);
  }
}

document.querySelectorAll('[data-download-trigger]').forEach((trigger) => {
  trigger.addEventListener('click', () => {
    if (!downloadDialog) return;
    if (typeof downloadDialog.showModal === 'function') downloadDialog.showModal();
    else downloadDialog.setAttribute('open', '');
    document.body.classList.add('has-download-dialog');
    resolveLatestDownloads();
  });
});

document.querySelector('[data-download-close]')?.addEventListener('click', closeDownloadDialog);
downloadDialog?.addEventListener('click', (event) => {
  if (event.target === downloadDialog) closeDownloadDialog();
});
downloadDialog?.addEventListener('close', () => {
  document.body.classList.remove('has-download-dialog');
});
