import fs from 'fs';
import path from 'path';
import { validatePatch } from './validate-patch.mjs';

const promptBundlePath = process.argv[2];
if (!promptBundlePath) {
    console.error('Usage: apply-ai-patch.mjs <prompt-bundle-path>');
    process.exit(1);
}

const openAiKey = process.env.OPENAI_API_KEY;
if (!openAiKey) {
    console.error('OPENAI_API_KEY is not set');
    process.exit(1);
}

async function main() {
    const bundleStr = fs.readFileSync(promptBundlePath, 'utf8');
    const bundle = JSON.parse(bundleStr);

    console.log(`Loaded prompt bundle for run ${bundle.runId}`);
    
    // Call OpenAI
    console.log('Calling OpenAI to generate patch...');
    const response = await fetch('https://api.openai.com/v1/chat/completions', {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json',
            'Authorization': `Bearer ${openAiKey}`
        },
        body: JSON.stringify({
            model: 'gpt-4o', // Or whatever model is preferred
            messages: [
                {
                    role: 'system',
                    content: 'You are an expert software engineer fixing bugs in the Argus Engine repository. You must return your fix as a structured JSON object according to the requested schema. Ensure all paths are relative to the repository root.'
                },
                {
                    role: 'user',
                    content: bundle.promptText
                }
            ],
            response_format: {
                type: 'json_schema',
                json_schema: {
                    name: 'ai_bug_fix_patch',
                    schema: {
                        type: 'object',
                        required: ['summary', 'files', 'tests', 'risks'],
                        additionalProperties: false,
                        properties: {
                            summary: { type: 'string' },
                            files: {
                                type: 'array',
                                maxItems: bundle.maxFiles || 30,
                                items: {
                                    type: 'object',
                                    required: ['path', 'action', 'content'],
                                    additionalProperties: false,
                                    properties: {
                                        path: { type: 'string' },
                                        action: { type: 'string', enum: ['add_or_update', 'delete'] },
                                        content: { type: 'string' }
                                    }
                                }
                            },
                            tests: {
                                type: 'array',
                                items: { type: 'string' }
                            },
                            risks: {
                                type: 'array',
                                items: { type: 'string' }
                            }
                        }
                    },
                    strict: true
                }
            },
            temperature: 0.2
        })
    });

    if (!response.ok) {
        const err = await response.text();
        console.error(`OpenAI API failed: ${response.status} ${err}`);
        process.exit(1);
    }

    const data = await response.json();
    const contentStr = data.choices[0].message.content;
    const patch = JSON.parse(contentStr);

    console.log('Received patch from AI. Validating...');
    
    // Validate Patch
    const validationErrors = validatePatch(patch, bundle);
    if (validationErrors.length > 0) {
        console.error('Patch validation failed:');
        validationErrors.forEach(err => console.error(`- ${err}`));
        
        // Report failure to maintenance API via report-status logic, or just let workflow fail
        // The workflow has a failure step that will catch this process.exit(1)
        process.exit(1);
    }

    console.log('Patch validated. Applying files...');

    // Apply files
    for (const file of patch.files) {
        const fullPath = path.resolve(process.cwd(), file.path);
        
        // Final safety check to ensure it doesn't escape cwd
        if (!fullPath.startsWith(process.cwd())) {
            console.error(`Safety violation: Path escapes cwd: ${file.path}`);
            process.exit(1);
        }

        if (file.action === 'delete') {
            if (fs.existsSync(fullPath)) {
                fs.rmSync(fullPath);
                console.log(`Deleted: ${file.path}`);
            }
        } else { // add_or_update
            const dir = path.dirname(fullPath);
            if (!fs.existsSync(dir)) {
                fs.mkdirSync(dir, { recursive: true });
            }
            fs.writeFileSync(fullPath, file.content, 'utf8');
            console.log(`Updated/Added: ${file.path}`);
        }
    }

    // Create PR Body
    let prBody = `## AI Bug Fix Summary\n\n${patch.summary}\n\n`;
    prBody += `### Modified Files\n`;
    for (const f of patch.files) {
        prBody += `- \`${f.action}\`: \`${f.path}\`\n`;
    }
    
    if (patch.tests && patch.tests.length > 0) {
        prBody += `\n### Tests Run\n`;
        for (const t of patch.tests) {
            prBody += `- \`${t}\`\n`;
        }
    }

    if (patch.risks && patch.risks.length > 0) {
        prBody += `\n### Risks / Notes\n`;
        for (const r of patch.risks) {
            prBody += `- ${r}\n`;
        }
    }

    fs.writeFileSync('/tmp/argus-ai-bugfix-pr-body.md', prBody, 'utf8');
    console.log('Successfully applied patch and generated PR body.');
}

main().catch(err => {
    console.error('Unhandled error:', err);
    process.exit(1);
});
