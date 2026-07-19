(function () {
    'use strict';

    var STORAGE_PREFIX = 'legend-factfinder-collapse:v1';

    function getRoots() {
        return Array.from(
            document.querySelectorAll('#ffSeniorApp, #ffMiddleApp, #ffYoungApp')
        );
    }

    function getStepBlocks(stepEl) {
        return Array.from(
            stepEl.querySelectorAll('.section-card .block')
        ).filter(function (block) {
            if (block.classList.contains('ff-no-collapse')) {
                return false;
            }

            return (
                block.dataset.ffCollapseReady === 'true' ||
                !!block.querySelector(':scope > .block-title')
            );
        });
    }

    function getStorageKey(root, stepIndex, blockIndex) {
        return [
            STORAGE_PREFIX,
            root.id || 'factfinder',
            String(stepIndex),
            String(blockIndex)
        ].join(':');
    }

    function readStoredState(storageKey) {
        try {
            var storedValue = window.sessionStorage.getItem(storageKey);

            if (storedValue === 'open') {
                return true;
            }

            if (storedValue === 'closed') {
                return false;
            }
        } catch (error) {
            // Session storage may be unavailable in restricted browser modes.
        }

        return null;
    }

    function writeStoredState(storageKey, open) {
        try {
            window.sessionStorage.setItem(
                storageKey,
                open ? 'open' : 'closed'
            );
        } catch (error) {
            // Collapse behavior still works when storage is unavailable.
        }
    }

    function setOpen(block, open, persist) {
        block.classList.toggle('is-open', open);

        var toggle = block.querySelector(':scope > .ff-collapse-toggle');
        var body = block.querySelector(':scope > .ff-collapse-body');

        if (toggle) {
            toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
        }

        if (body) {
            body.hidden = !open;
        }

        if (persist && block.dataset.ffCollapseStorageKey) {
            writeStoredState(block.dataset.ffCollapseStorageKey, open);
        }
    }

    function initializeBlock(block, storageKey) {
        block.dataset.ffCollapseStorageKey = storageKey;

        if (block.dataset.ffCollapseReady === 'true') {
            return;
        }

        var titleEl = block.querySelector(':scope > .block-title');

        if (!titleEl) {
            return;
        }

        var titleText = titleEl.textContent.trim();

        var toggle = document.createElement('button');
        toggle.type = 'button';
        toggle.className = 'ff-collapse-toggle';
        toggle.setAttribute('aria-expanded', 'false');
        toggle.innerHTML =
            '<span class="ff-collapse-heading"></span>' +
            '<span class="ff-collapse-state" aria-hidden="true"></span>';

        toggle.querySelector('.ff-collapse-heading').textContent = titleText;

        var body = document.createElement('div');
        body.className = 'ff-collapse-body';
        body.hidden = true;

        var contentNodes = Array.from(block.children).filter(function (child) {
            return child !== titleEl;
        });

        titleEl.remove();

        contentNodes.forEach(function (child) {
            body.appendChild(child);
        });

        block.classList.add('ff-collapse');
        block.insertAdjacentElement('afterbegin', toggle);
        block.appendChild(body);
        block.dataset.ffCollapseReady = 'true';

        toggle.addEventListener('click', function () {
            var shouldOpen = !block.classList.contains('is-open');
            setOpen(block, shouldOpen, true);
        });
    }

    function syncRoot(root) {
        if (!root) {
            return;
        }

        var steps = Array.from(root.querySelectorAll('.wizard-step'));

        var activeIndex = steps.findIndex(function (step) {
            return step.classList.contains('active');
        });

        steps.forEach(function (step, stepIndex) {
            var blocks = getStepBlocks(step);

            blocks.forEach(function (block, blockIndex) {
                var storageKey = getStorageKey(
                    root,
                    stepIndex,
                    blockIndex
                );

                initializeBlock(block, storageKey);

                if (stepIndex !== activeIndex) {
                    setOpen(block, false, false);
                    return;
                }

                var storedState = readStoredState(storageKey);

                var shouldOpenByDefault =
                    activeIndex === 0 &&
                    stepIndex === 0 &&
                    blockIndex === 0;

                var shouldOpen =
                    storedState === null
                        ? shouldOpenByDefault
                        : storedState;

                setOpen(block, shouldOpen, false);
            });
        });
    }

    function revealForElement(element) {
        if (!element) {
            return;
        }

        var block = element.closest('.ff-collapse');

        if (block) {
            setOpen(block, true, true);
        }
    }

    function initRoot(root) {
        if (!root) {
            return;
        }

        if (root.dataset.ffSectionsBound === 'true') {
            syncRoot(root);
            return;
        }

        root.dataset.ffSectionsBound = 'true';

        root.addEventListener(
            'invalid',
            function (event) {
                revealForElement(event.target);
            },
            true
        );

        syncRoot(root);
    }

    function initAll() {
        getRoots().forEach(initRoot);
    }

    window.LegendFactFinderSections = {
        initAll: initAll,
        initRoot: initRoot,
        revealForElement: revealForElement,
        sync: syncRoot
    };

    if (document.readyState === 'loading') {
        document.addEventListener(
            'DOMContentLoaded',
            initAll,
            { once: true }
        );
    } else {
        initAll();
    }
})();
