// Sidebar section-stack sizing (owner batch 2026-07-26): expanded sections split the sidebar height
// by default percentages (per page), drag handles between adjacent expanded sections resize them,
// and user weights persist per page + per visible-set (so "search open" and "everything open" each
// remember their own layout). User weights always beat the defaults. Collapsed sections keep only
// their header. Re-applied after every Blazor render (handles are re-injected idempotently).
window.fravashiSections = (function () {
    'use strict';

    const HANDLE_CLASS = 'section-resize-handle';
    const MIN_PX = 64;

    function isExpanded(section) {
        const header = section.querySelector('.page-sidebar-section-header');
        return header && header.getAttribute('aria-expanded') === 'true';
    }

    function idOf(section) {
        return section.getAttribute('data-testid') || 'section';
    }

    function load(storageKey) {
        try {
            const raw = localStorage.getItem(storageKey);
            return raw ? JSON.parse(raw) : {};
        } catch (e) {
            return {};
        }
    }

    function save(storageKey, value) {
        try {
            localStorage.setItem(storageKey, JSON.stringify(value));
        } catch (e) {
            // Sizing persistence is best effort.
        }
    }

    function visibleSetKey(expanded) {
        return expanded.map(idOf).sort().join('+');
    }

    function computeWeights(expanded, defaults, stored) {
        const weights = {};
        if (expanded.length === 1) {
            weights[idOf(expanded[0])] = 100;
            return weights;
        }

        let remaining = 100;
        const unlisted = [];
        expanded.forEach(function (section) {
            const d = defaults[idOf(section)];
            if (typeof d === 'number') {
                weights[idOf(section)] = d;
                remaining -= d;
            } else {
                unlisted.push(section);
            }
        });
        if (unlisted.length > 0) {
            const each = Math.max(remaining, 5 * unlisted.length) / unlisted.length;
            unlisted.forEach(function (section) { weights[idOf(section)] = each; });
        } else if (remaining !== 0) {
            const used = 100 - remaining;
            expanded.forEach(function (section) { weights[idOf(section)] = weights[idOf(section)] * 100 / used; });
        }

        // A user-dragged layout for THIS combination of open sections overrides the defaults.
        const user = stored[visibleSetKey(expanded)];
        if (user) {
            expanded.forEach(function (section) {
                if (typeof user[idOf(section)] === 'number') {
                    weights[idOf(section)] = user[idOf(section)];
                }
            });
        }

        return weights;
    }

    function attachDrag(handle, container, storageKey) {
        handle.addEventListener('pointerdown', function (down) {
            down.preventDefault();
            handle.setPointerCapture(down.pointerId);
            const above = handle.previousElementSibling;
            let below = handle.nextElementSibling;
            while (below && below.classList.contains(HANDLE_CLASS)) {
                below = below.nextElementSibling;
            }
            if (!above || !below) {
                return;
            }
            const startY = down.clientY;
            const startAbove = above.getBoundingClientRect().height;
            const startBelow = below.getBoundingClientRect().height;
            const expanded = [...container.querySelectorAll('.page-sidebar-section')].filter(isExpanded);
            const startPx = new Map(expanded.map(function (s) { return [s, s.getBoundingClientRect().height]; }));

            function onMove(move) {
                const delta = Math.max(
                    MIN_PX - startAbove,
                    Math.min(move.clientY - startY, startBelow - MIN_PX));
                expanded.forEach(function (section) {
                    let px = startPx.get(section);
                    if (section === above) {
                        px += delta;
                    } else if (section === below) {
                        px -= delta;
                    }
                    section.style.flex = px + ' 1 0px';
                });
            }

            function onUp() {
                handle.removeEventListener('pointermove', onMove);
                handle.removeEventListener('pointerup', onUp);
                // Persist as percentages for this visible set.
                const total = expanded.reduce(function (sum, s) { return sum + s.getBoundingClientRect().height; }, 0);
                if (total > 0) {
                    const stored = load(storageKey);
                    const weights = {};
                    expanded.forEach(function (s) {
                        weights[idOf(s)] = Math.round(s.getBoundingClientRect().height / total * 1000) / 10;
                    });
                    stored[visibleSetKey(expanded)] = weights;
                    save(storageKey, stored);
                }
            }

            handle.addEventListener('pointermove', onMove);
            handle.addEventListener('pointerup', onUp);
        });
    }

    function layout(container, storageKey, defaults) {
        container.querySelectorAll('.' + HANDLE_CLASS).forEach(function (h) { h.remove(); });
        const sections = [...container.querySelectorAll('.page-sidebar-section')];
        const expanded = sections.filter(isExpanded);
        const weights = computeWeights(expanded, defaults, load(storageKey));

        sections.forEach(function (section) {
            if (isExpanded(section)) {
                section.style.flex = (weights[idOf(section)] || 10) + ' 1 0px';
                section.style.minHeight = '64px';
            } else {
                section.style.flex = '0 0 auto';
                section.style.minHeight = '0';
            }
        });

        for (let i = 0; i < expanded.length - 1; i++) {
            const handle = document.createElement('div');
            handle.className = HANDLE_CLASS;
            handle.setAttribute('role', 'separator');
            handle.setAttribute('aria-label', 'Resize sections');
            expanded[i + 1].parentNode.insertBefore(handle, expanded[i + 1]);
            attachDrag(handle, container, storageKey);
        }
    }

    // Collapse toggles re-render only the section component, never the page, so the page-level
    // re-apply from OnAfterRender does not fire. The observer keeps the layout honest: any
    // aria-expanded flip (or sections appearing/disappearing on a tab switch) re-runs layout.
    // Handle churn from layout itself is filtered out so it cannot feed back.
    function observe(container, storageKey, defaults) {
        container.__fravashiSectionsConfig = { storageKey: storageKey, defaults: defaults };
        if (container.__fravashiSectionsObserved) {
            return;
        }
        container.__fravashiSectionsObserved = true;
        let scheduled = false;
        const observer = new MutationObserver(function (mutations) {
            const relevant = mutations.some(function (m) {
                if (m.type === 'attributes') {
                    return true; // aria-expanded only (attributeFilter below)
                }
                const nodes = [...m.addedNodes, ...m.removedNodes];
                return nodes.some(function (n) {
                    return n.nodeType === 1
                        && !n.classList.contains(HANDLE_CLASS)
                        && (n.classList.contains('page-sidebar-section')
                            || (n.querySelector && n.querySelector('.page-sidebar-section')));
                });
            });
            if (!relevant || scheduled) {
                return;
            }
            scheduled = true;
            requestAnimationFrame(function () {
                scheduled = false;
                const cfg = container.__fravashiSectionsConfig;
                layout(container, cfg.storageKey, cfg.defaults);
            });
        });
        observer.observe(container, {
            subtree: true,
            childList: true,
            attributes: true,
            attributeFilter: ['aria-expanded']
        });
    }

    return {
        apply: function (containerSelector, storageKey, defaults) {
            const container = document.querySelector(containerSelector);
            if (!container) {
                return;
            }

            observe(container, storageKey, defaults || {});
            layout(container, storageKey, defaults || {});
        }
    };
})();
