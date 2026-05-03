<truncated 78 lines>
        totalSkippedEmptyOrInvalid += skippedEmptyOrInvalid;
        totalSkippedDuplicateInBatch += skippedDuplicateInBatch;

        await dotNetRef.invokeMethodAsync(
            "OnBulkImportChunkCompleted",
            index + 1,
            chunksTotal,
            created,
            skippedAlreadyExist,
            skippedEmptyOrInvalid,
            skippedDuplicateInBatch,
            totalCreated,
            totalSkippedAlreadyExist,
            totalSkippedEmptyOrInvalid,
            totalSkippedDuplicateInBatch);
    }

    return {
        created: totalCreated,
        skippedAlreadyExist: totalSkippedAlreadyExist,
        skippedEmptyOrInvalid: totalSkippedEmptyOrInvalid,
        skippedDuplicateInBatch: totalSkippedDuplicateInBatch,
        Created: totalCreated,
        SkippedAlreadyExist: totalSkippedAlreadyExist,
        SkippedEmptyOrInvalid: totalSkippedEmptyOrInvalid,
        SkippedDuplicateInBatch: totalSkippedDuplicateInBatch
    };
};
