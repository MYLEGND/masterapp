(() => {
  'use strict';
  // Source marker only: the shared catalog and DOM localization remain authoritative.
  const applicationCopy = value => value;
  const root = document.querySelector('[data-social-original]');
  if (!root) return;
  const postUrl = `/Social/Posts/${encodeURIComponent(root.dataset.postId)}`;
  const status = root.querySelector('[data-social-status]');
  let busy = false;
  let sharing = false;
  const text = (tag, value, className = '') => {
    const node = document.createElement(tag);
    node.textContent = value || '';
    node.className = className;
    return node;
  };
  function linkText(node) {
    const value = node.textContent;
    const fragment = document.createDocumentFragment();
    let offset = 0;
    for (const match of value.matchAll(/https?:\/\/[^\s<>]+/gi)) {
      const target = match[0].replace(/[.,!?;:)]+$/, '');
      fragment.append(document.createTextNode(value.slice(offset, match.index)));
      try {
        const url = new URL(target);
        const link = text('a', target);
        link.href = url.href;
        if (url.origin !== location.origin) { link.target = '_blank'; link.rel = 'noopener noreferrer'; }
        fragment.append(link);
      } catch (_) { fragment.append(document.createTextNode(target)); }
      offset = match.index + target.length;
    }
    fragment.append(document.createTextNode(value.slice(offset)));
    node.replaceChildren(fragment);
  }
  function setStatus(message, failed = false) {
    status.textContent = message;
    status.classList.toggle('is-error', failed);
  }
  function cancelReply() {
    const form = root.querySelector('[data-social-action="comments"]');
    if (!form) return;
    form.elements.ParentCommentId.value = '';
    root.querySelector('[data-social-replying]').hidden = true;
  }
  function bindMediaError(media) {
    media.addEventListener('error', () => {
      media.replaceWith(text('p', applicationCopy("This media could not be loaded. Refresh the original to check availability.")));
    });
  }
  function renderMedia(assets) {
    const container = root.querySelector('[data-social-media]');
    const media = (assets || []).filter(asset => ['Image', 'Video'].includes(asset.mediaKind))
      .slice().sort((a, b) => a.displayOrder - b.displayOrder);
    const existing = [...container.querySelectorAll('[data-social-media-id]')].map(node => node.dataset.socialMediaId.toLowerCase());
    if (existing.join('|') === media.map(asset => String(asset.id).toLowerCase()).join('|')) return;
    const fragment = document.createDocumentFragment();
    media.forEach(asset => {
      const node = document.createElement(asset.mediaKind === 'Image' ? 'img' : 'video');
      node.dataset.socialMediaId = asset.id;
      node.dataset.userContent = '';
      node.src = `/Social/Media/${encodeURIComponent(asset.id)}`;
      if (asset.mediaKind === 'Image') { node.alt = asset.accessibilityText || applicationCopy("Post image"); node.loading = 'lazy'; }
      else { node.controls = true; node.playsInline = true; node.preload = 'metadata'; node.setAttribute('aria-label', asset.accessibilityText || applicationCopy("Post video")); }
      bindMediaError(node);
      fragment.append(node);
    });
    container.replaceChildren(fragment);
  }
  function renderPost(post) {
    const caption = root.querySelector('[data-social-post-body]');
    caption.textContent = post.body || '';
    caption.hidden = !post.body;
    linkText(caption);
    renderMedia(post.media);
    root.querySelector('[data-social-reactions]').textContent = post.reactionCount;
    root.querySelector('[data-social-comment-count]').textContent = post.commentCount;
    root.querySelector('[data-social-reaction-label]').textContent = post.reactedByCurrentActor ? applicationCopy("Liked") : applicationCopy("Like");
    root.querySelector('[data-social-react]').setAttribute('aria-pressed', String(post.reactedByCurrentActor));
    const save = root.querySelector('[data-social-save]');
    save.textContent = post.savedByCurrentActor ? applicationCopy("Saved") : applicationCopy("Save");
    save.setAttribute('aria-pressed', String(post.savedByCurrentActor));
    const repost = root.querySelector('[data-social-repost]');
    repost.textContent = post.repostedByCurrentActor ? applicationCopy("Reposted") : applicationCopy("Repost");
    repost.setAttribute('aria-pressed', String(post.repostedByCurrentActor));
    root.querySelector('[data-social-share-count]').textContent = post.metrics.shareCount;
    root.querySelector('[data-social-repost-count]').textContent = post.metrics.repostCount;
    const list = root.querySelector('[data-social-comments]');
    const comments = document.createDocumentFragment();
    (post.comments || []).forEach(comment => {
      const card = text('article', '', 'social-original-comment' + (comment.parentCommentId ? ' is-reply' : ''));
      card.id = `social-comment-${comment.id}`;
      const header = text('header', '');
      const time = text('time', new Date(comment.createdUtc).toLocaleString());
      time.dateTime = comment.createdUtc;
      const author = text('strong', comment.author.displayName);
      author.dataset.userContent = '';
      header.append(author, time);
      card.append(header);
      if (comment.parentCommentId) {
        const parent = text('a', applicationCopy("View parent comment"), 'social-original-reply-context');
        parent.href = `#social-comment-${comment.parentCommentId}`;
        card.append(parent);
      }
      const body = text('p', comment.body);
      body.dataset.userContent = '';
      linkText(body);
      card.append(body);
      if (post.commentsEnabled) {
        const reply = text('button', applicationCopy("Reply"));
        reply.type = 'button'; reply.dataset.socialReply = comment.id; reply.dataset.author = comment.author.displayName;
        card.append(reply);
      }
      comments.append(card);
    });
    list.replaceChildren(comments);
    root.querySelector('[data-social-no-comments]')?.remove();
    if (!(post.comments || []).length) {
      const empty = text('p', applicationCopy("No comments yet.")); empty.dataset.socialNoComments = ''; list.append(empty);
    }
    const form = root.querySelector('[data-social-action="comments"]');
    if (form) form.hidden = !post.commentsEnabled;
  }
  async function mutate(action, data) {
    const response = await fetch(`${postUrl}/${action}`, {
      method: 'POST', credentials: 'same-origin', body: data,
      headers: { Accept: 'application/json', 'X-Requested-With': 'XMLHttpRequest' },
      signal: AbortSignal.timeout(20000)
    });
    if (!response.ok) {
      if ([401,403,404,410].includes(response.status)) {
        root.querySelector('[data-social-post]').replaceChildren();
        root.querySelector('[data-social-comments-section]').replaceChildren();
        root.querySelector('.social-original-identity').replaceChildren();
        throw new Error(applicationCopy("This original content is no longer available to this account."));
      }
      let message = applicationCopy("The action could not be confirmed. Refresh the original before retrying.");
      try { const failure = await response.json(); message = failure.errorMessage || failure.message || message; } catch (_) {}
      throw new Error(message);
    }
    const post = await response.json();
    if (String(post.id).toLowerCase() !== root.dataset.postId.toLowerCase()) throw new Error(applicationCopy("The response did not match this original post."));
    renderPost(post);
  }
  async function perform(action, data, completed) {
    if (busy || sharing) return;
    busy = true;
    const buttons = [...root.querySelectorAll('button')];
    buttons.forEach(button => button.disabled = true);
    setStatus(applicationCopy("Saving…"));
    try {
      await mutate(action, data);
      completed?.();
      setStatus(applicationCopy("Updated."));
    } catch (error) {
      setStatus(error.name === 'TimeoutError' ? applicationCopy("The action timed out and may have completed. Refresh before retrying.") : error.message, true);
    } finally {
      busy = false;
      buttons.forEach(button => { if (button.isConnected) button.disabled = false; });
    }
  }
  root.addEventListener('submit', event => {
    const form = event.target.closest('[data-social-action]');
    if (!form) return;
    event.preventDefault();
    const body = form.elements.Body;
    const submittedBody = body?.value;
    void perform(form.dataset.socialAction, new FormData(form), () => {
      if (body && body.value === submittedBody) { body.value = ''; cancelReply(); }
    });
  });
  root.addEventListener('click', async event => {
    const reply = event.target.closest('[data-social-reply]');
    if (reply) {
      const form = root.querySelector('[data-social-action="comments"]');
      if (!form) return;
      form.elements.ParentCommentId.value = reply.dataset.socialReply;
      const context = root.querySelector('[data-social-replying]');
      const name = text('span', reply.dataset.author);
      name.dataset.userContent = '';
      context.querySelector('span').replaceChildren(text('span', applicationCopy("Replying to")), document.createTextNode(' '), name);
      context.hidden = false; form.elements.Body.focus();
    }
    if (event.target.closest('[data-social-cancel-reply]')) cancelReply();
    if (!event.target.closest('[data-social-share]') || busy || sharing) return;
    sharing = true;
    const url = new URL(postUrl, location.origin).href;
    try {
      if (navigator.share) await navigator.share({ title: document.title, url });
      else if (navigator.clipboard?.writeText) await navigator.clipboard.writeText(url);
      else { setStatus(applicationCopy("Sharing is unavailable in this browser. Copy the page address to share the original."), true); return; }
    } catch (error) { if (error.name !== 'AbortError') setStatus(applicationCopy("Sharing could not be completed."), true); return; }
    finally { sharing = false; }
    const data = new FormData();
    data.set('__RequestVerificationToken', root.querySelector('input[name="__RequestVerificationToken"]').value);
    void perform('share', data);
  });
  root.querySelectorAll('[data-social-body]').forEach(linkText);
  root.querySelectorAll('[data-social-media] img, [data-social-media] video').forEach(bindMediaError);
})();
