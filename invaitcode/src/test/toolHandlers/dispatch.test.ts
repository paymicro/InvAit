/**
 * Unit tests for dispatchTool (toolHandlers/index.ts).
 */
import * as chai from 'chai';
import chaiAsPromised = require('chai-as-promised');
import { toolHandlers, WORKSPACE, resetStubs } from './setup';

chai.use(chaiAsPromised);
const expect = chai.expect;

describe('dispatchTool', () => {
    afterEach(() => resetStubs());

    it('should return error for unknown action', () => {
        const result = toolHandlers.dispatchTool('unknown_action', '{}', WORKSPACE);
        expect(result.success).to.be.false;
        expect(result.error).to.include('Unknown action');
    });

    it('should return error for read_open_file (unknown action)', () => {
        const result = toolHandlers.dispatchTool('read_open_file', '{}', WORKSPACE);
        expect(result.success).to.be.false;
        expect(result.error).to.include('Unknown action');
    });

    it('should return error for get_error_list (unknown action)', () => {
        const result = toolHandlers.dispatchTool('get_error_list', '{}', WORKSPACE);
        expect(result.success).to.be.false;
        expect(result.error).to.include('Unknown action');
    });

    it('should handle invalid JSON payload gracefully', () => {
        const result = toolHandlers.dispatchTool('read_files', 'not json', WORKSPACE);
        expect(result.success).to.be.false;
        expect(result.error).to.be.a('string');
    });
});
