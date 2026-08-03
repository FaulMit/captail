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
