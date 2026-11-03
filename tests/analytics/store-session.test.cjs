const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync('ParfaitApp/wwwroot/js/store-session.js', 'utf8');

test('handoff navigates once and prevents duplicate redemption', () => {
    let handler, navigations = 0;
    const button = { hidden: false };
    const form = {
        dataset: {},
        querySelector: () => button,
        addEventListener: (_, callback) => { handler = callback; },
        requestSubmit() {
            const event = { prevented: false, preventDefault() { this.prevented = true; } };
            handler(event);
            if (!event.prevented) navigations++;
        }
    };
    const context = { document: { getElementById: () => form } };
    vm.runInNewContext(source, context);
    assert.equal(navigations, 1);
    assert.equal(button.hidden, true);
    form.requestSubmit();
    vm.runInNewContext(source, context);
    assert.equal(navigations, 1);
});

test('handoff leaves unrelated pages untouched', () => {
    vm.runInNewContext(source, { document: { getElementById: () => null } });
});
