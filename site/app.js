document.querySelectorAll('[data-year]').forEach((node) => {
  node.textContent = new Date().getFullYear();
});

const tourImage = document.querySelector('#showcase-image');
const tourTitle = document.querySelector('#showcase-window-title');
document.querySelectorAll('.showcase-tab').forEach((button) => {
  button.addEventListener('click', () => {
    if (!tourImage || !tourTitle) return;
    document.querySelectorAll('.showcase-tab').forEach((tab) => {
      const selected = tab === button;
      tab.classList.toggle('is-active', selected);
      tab.setAttribute('aria-pressed', String(selected));
    });
    tourImage.src = button.dataset.image;
    tourImage.alt = button.dataset.alt;
    tourTitle.textContent = button.dataset.title;
  });
});

document.querySelectorAll('[data-copy]').forEach((button) => {
  button.addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText(button.dataset.copy);
      const previousText = button.textContent;
      button.textContent = 'Copied';
      window.setTimeout(() => { button.textContent = previousText; }, 1800);
    } catch {
      button.textContent = 'Select text';
      window.setTimeout(() => { button.textContent = 'Copy'; }, 1800);
    }
  });
});
