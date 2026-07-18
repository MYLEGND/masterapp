(function () {
    function getRoots() {
        return Array.from(document.querySelectorAll('#ffSeniorApp, #ffMiddleApp, #ffYoungApp'));
    }

    function getStepBlocks(stepEl) {
        return Array.from(stepEl.querySelectorAll('.section-card .block')).filter(function (block) {
            if (block.classList.contains('ff-no-collapse')) {
                return false;
            }

            return !!block.querySelector(':scope > .block-title');
        });
    }

    function setOpen(block, open, manual) {
        block.classList.toggle('is-open', open);

        var toggle = block.querySelector(':scope > .ff-collapse-toggle');
        var body = block.querySelector(':scope > .ff-collapse-body');

        if (toggle) {
            toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
        }

        if (body) {
            body.hidden = !open;
        }

        if (manual) {
            block.dataset.ffCollapseManual = 'true';
            block.dataset.ffCollapseOpen = open ? 'true' : 'false';
        }
    }

    function initializeBlock(block) {
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
        toggle.innerHTML =
            '<span class="ff-collapse-heading"></span>' +
            '<span class="ff-collapse-meta" aria-hidden="true"></span>' +
            '<span class="ff-collapse-chevron" aria-hidden="true"></span>';
        toggle.querySelector('.ff-collapse-heading').textContent = titleText;

        var body = document.createElement('div');
        body.className = 'ff-collapse-body';

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
            setOpen(block, !block.classList.contains('is-open'), true);
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
                initializeBlock(block);

                var isManual = block.dataset.ffCollapseManual === 'true';
                var manualOpen = block.dataset.ffCollapseOpen === 'true';
                var shouldOpenByDefault = activeIndex === 0 && stepIndex === 0 && blockIndex === 0;
                var shouldOpen = false;

                if (stepIndex === activeIndex) {
                    shouldOpen = isManual ? manualOpen : shouldOpenByDefault;
                }

                setOpen(block, shouldOpen, false);
            });
        });
    }

    function revealForElement(el) {
        if (!el) {
            return;
        }

        var block = el.closest('.ff-collapse');
        if (block) {
            setOpen(block, true, true);
        }
    }

    function initRoot(root) {
        if (!root || root.dataset.ffSectionsBound === 'true') {
            syncRoot(root);
            return;
        }

        root.dataset.ffSectionsBound = 'true';

        root.addEventListener('focusin', function (event) {
            revealForElement(event.target);
        });

        root.addEventListener('invalid', function (event) {
            revealForElement(event.target);
        }, true);

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
        document.addEventListener('DOMContentLoaded', initAll, { once: true });
    } else {
        initAll();
    }
})();
