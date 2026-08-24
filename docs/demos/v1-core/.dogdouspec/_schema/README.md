# Demo Schema Directory

The normative development schemas now live under the repository root
`schemas/v1`. During `workspace init`, the CLI copies matching readable schemas
into this visible directory so an Agent can inspect the contract without a
project catalog. The CLI's embedded schemas remain the validation authority;
workspace copies cannot silently redefine v1.

The demo avoids duplicating the same XSD text. Schema validation tests bind its
XML documents directly to the normative repository copies.
