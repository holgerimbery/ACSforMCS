# DTMF Recognition Implementation

This document describes the DTMF (Dual-Tone Multi-Frequency) recognition functionality added to the ACSforMCS system.

## Overview

The DTMF recognition system captures and processes phone keypad input (0-9, *, #) during calls and integrates this information with the bot conversation system for interactive voice response (IVR) and routing capabilities.

## Key Components

### 1. CallContext Enhancements

The `CallContext` class now includes:

- **`DtmfSequence`**: Stores the complete sequence of DTMF tones detected during the call
- **`HasDtmfInput`**: Boolean flag indicating whether any DTMF input has been detected
- **`AddDtmfTone(string tone)`**: Thread-safe method to append DTMF tones to the sequence
- **Enhanced `GetDataQuality()`**: Now returns "complete" when both caller info and DTMF input are available

### 2. DtmfExtractor Service

A comprehensive service following the same pattern as `CallerInfoExtractor`:

- **`DtmfInfo`**: Data transfer object containing extracted DTMF information
- **`ExtractDtmfFromRecognizeEvent()`**: Processes Azure Communication Services `RecognizeCompleted` events
- **Utility methods**: Validation, formatting, and status checking for DTMF sequences

### 3. Event Processing

The webhook endpoint in `Program.cs` now handles:

- **`RecognizeCompleted` events**: Automatically processes DTMF recognition results
- **Bot integration**: Sends DTMF information to bot conversations in real-time
- **Comprehensive logging**: Tracks DTMF detection and processing

## Usage Examples

### Bot Message Format

When DTMF tones are detected, the system sends messages to the bot in this format:
```
DTMF_INPUT=5|DTMF_SEQUENCE=12345
```

Where:
- `DTMF_INPUT`: The most recently detected tone
- `DTMF_SEQUENCE`: The complete sequence of tones detected during the call

### CallContext Usage

```csharp
// Check if DTMF input is available
if (callContext.HasDtmfInput)
{
    var sequence = callContext.DtmfSequence;
    // Use sequence for routing or validation
}

// Add DTMF tone (handled automatically by the system)
callContext.AddDtmfTone("1");
```

### DtmfExtractor Usage

```csharp
// Validate DTMF sequence
bool isValid = DtmfExtractor.IsValidDtmfSequence("123*#"); // returns true
bool isValid2 = DtmfExtractor.IsValidDtmfSequence("123ABC"); // returns false

// Format for display
string formatted = DtmfExtractor.FormatDtmfSequence("123*#"); // returns "1 2 3 * #"
```

## Integration with Bots

Bots can now:

1. **Receive DTMF input**: Process real-time DTMF messages for IVR navigation
2. **Implement menu systems**: Use DTMF sequences for multi-level menu navigation
3. **Validate input**: Check DTMF sequences for authentication or data entry
4. **Route calls**: Direct calls based on DTMF input to appropriate departments

## Data Quality Levels

The system now provides enhanced data quality assessment:

- **"complete"**: Caller info, callee info, and DTMF input all available
- **"enhanced"**: Caller and callee info available (no DTMF yet)
- **"partial"**: Either caller or callee info available
- **"minimal"**: No caller/callee information available

## Testing

A development-only test endpoint is available at `/test/dtmf` that demonstrates:

- DTMF sequence building
- Validation functionality  
- Integration with CallContext
- Error handling scenarios

## Error Handling

The system provides robust error handling for:

- Invalid DTMF tones
- Recognition failures
- Bot communication errors
- Thread safety during concurrent access

## Logging

Comprehensive logging tracks:

- DTMF tone recognition events
- Sequence building progress
- Bot message delivery
- Error conditions and recovery

This implementation enables rich interactive voice experiences while maintaining the existing caller identification and routing capabilities.