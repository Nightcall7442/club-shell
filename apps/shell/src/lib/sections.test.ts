import assert from 'node:assert/strict';
import { test } from 'node:test';
import { sectionIndex, slideDirection } from './sections.ts';

const ORDER = ['/home', '/games', '/shop', '/wallet'];

test('a section and its sub-pages share the place of the section in the menu', () => {
  assert.equal(sectionIndex('/games', ORDER), 1);
  assert.equal(sectionIndex('/games/cs2', ORDER), 1);
  assert.equal(sectionIndex('/gamesx', ORDER), ORDER.length);
  assert.equal(sectionIndex('/profile', ORDER), ORDER.length);
});

test('the next screen comes from the side of its tab', () => {
  assert.equal(slideDirection(0, 2), 1);
  assert.equal(slideDirection(3, 1), -1);
  assert.equal(slideDirection(1, 1), 0);
});
