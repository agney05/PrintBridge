import test from 'node:test';
import assert from 'node:assert/strict';
import { canCancel, isExpired, matchesDocument } from '../src/validation.js';

test('pairing codes expire at the deadline',()=>{assert.equal(isExpired(99,100),false);assert.equal(isExpired(100,100),true);assert.equal(isExpired(101,100),true)});
test('only queued jobs are cancellable',()=>{assert.equal(canCancel('queued'),true);for(const s of ['printing','submitted','completed','failed','cancelled'])assert.equal(canCancel(s),false)});
test('content signatures must match document extension',()=>{assert.equal(matchesDocument('.pdf','application/pdf',Buffer.from('%PDF-')),true);assert.equal(matchesDocument('.png','application/pdf',Buffer.from('%PDF-')),false);assert.equal(matchesDocument('.txt',undefined,Buffer.from('hello')),true);assert.equal(matchesDocument('.txt',undefined,Buffer.from([0,1])),false);assert.equal(matchesDocument('.docx','application/zip',Buffer.from([0x50,0x4b])),true);assert.equal(matchesDocument('.docx','application/zip',Buffer.from('no')),false)});
