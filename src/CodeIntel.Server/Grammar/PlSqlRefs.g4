// Focused PL/SQL grammar for object-reference extraction.
//
// This is intentionally a *subset* grammar — it does not parse PL/SQL fully (an
// undertaking that would pull in the ~10k-line antlr/grammars-v4 grammar). What
// it *does* do is tokenize PL/SQL correctly (comments, single-quoted strings
// with embedded '', quoted identifiers, schema-qualified names) and then walk a
// loose statement stream looking for the constructs we care about for context
// resolution:
//
//   • table references after FROM / JOIN / INTO / UPDATE / DELETE [FROM] /
//     MERGE INTO / USING
//   • routine invocations via EXECUTE / EXEC / CALL <name>
//   • package.proc(...) call syntax
//
// The benefit over the prior regex-only parser:
//   - comments and string literals are stripped at the lexer level, so
//     `-- FROM customer` and `'INSERT INTO foo'` never produce false positives
//   - quoted identifiers ("My Table") tokenize correctly
//   - multi-line statements with comments interspersed work
//
// Everything outside our keywords is consumed by `anyToken` so the grammar
// stays robust against syntax we don't understand.

grammar PlSqlRefs;

// ── Parser rules ─────────────────────────────────────────────────────────

script: item* EOF;

item
    : tableRefStmt
    | explicitCall
    | qualifiedCall
    | anyToken
    ;

tableRefStmt
    : FROM     qualifiedName              # fromRef
    | JOIN     qualifiedName              # joinRef
    | INTO     qualifiedName              # intoRef
    | UPDATE   qualifiedName              # updateRef
    | DELETE   FROM? qualifiedName        # deleteRef
    | MERGE    INTO  qualifiedName        # mergeRef
    | USING    qualifiedName              # usingRef
    ;

explicitCall
    : (EXECUTE | EXEC | CALL) qualifiedName    # execCall
    ;

// Picks up `package.proc(...)` patterns specifically. Pure-identifier dotted
// names without a trailing paren are handled by tableRefStmt above.
qualifiedCall
    : IDENT '.' IDENT '('                       # pkgProcCall
    ;

qualifiedName
    : (IDENT | QUOTED_IDENT) ('.' (IDENT | QUOTED_IDENT))?
    ;

// Anything we don't care about — keeps the parser tolerant of unknown syntax.
// The new structural tokens (PROCEDURE, FUNCTION, CURSOR, EXCEPTION, WHEN, OTHERS,
// THEN, BEGIN_KW, END_KW, NULL_KW, IS_KW, AS_KW, RETURN_KW) are consumed here so the
// object-reference parser stays tolerant; structural metrics are computed by a
// separate lexer-token-stream walker that inspects these tokens directly.
anyToken
    : IDENT
    | QUOTED_IDENT
    | NUMBER
    | STRING
    | PUNCT
    | KEYWORD_OTHER
    | PROCEDURE
    | FUNCTION
    | CURSOR
    | EXCEPTION
    | WHEN
    | OTHERS
    | THEN
    | BEGIN_KW
    | END_KW
    | NULL_KW
    | IS_KW
    | AS_KW
    | RETURN_KW
    | IF_KW
    | ELSIF_KW
    | LOOP_KW
    | WHILE_KW
    | FOR_KW
    | CASE_KW
    | AND_KW
    | OR_KW
    | '.'
    | '('
    | ')'
    ;

// ── Lexer rules ──────────────────────────────────────────────────────────

// Order matters: keywords above the generic IDENT rule.
FROM    : F R O M;
JOIN    : J O I N;
INTO    : I N T O;
UPDATE  : U P D A T E;
DELETE  : D E L E T E;
MERGE   : M E R G E;
USING   : U S I N G;
EXECUTE : E X E C U T E;
EXEC    : E X E C;
CALL    : C A L L;

// Structural tokens used by the metrics analyzer's token-stream walker. The
// suffixed names (BEGIN_KW etc.) avoid colliding with ANTLR-reserved or
// commonly-used identifiers in tooling.
PROCEDURE : P R O C E D U R E ;
FUNCTION  : F U N C T I O N ;
CURSOR    : C U R S O R ;
EXCEPTION : E X C E P T I O N ;
WHEN      : W H E N ;
OTHERS    : O T H E R S ;
THEN      : T H E N ;
BEGIN_KW  : B E G I N ;
END_KW    : E N D ;
NULL_KW   : N U L L ;
IS_KW     : I S ;
AS_KW     : A S ;
RETURN_KW : R E T U R N ;

// Branching tokens used by the cyclomatic-complexity counter.
IF_KW     : I F ;
ELSIF_KW  : E L S I F ;
LOOP_KW   : L O O P ;
WHILE_KW  : W H I L E ;
FOR_KW    : F O R ;
CASE_KW   : C A S E ;
AND_KW    : A N D ;
OR_KW     : O R ;

KEYWORD_OTHER
    : (S E L E C T)
    | (I N S E R T)
    | (W H E R E)
    | (G R O U P)
    | (O R D E R)
    | (B Y)
    ;

// Standard Oracle identifiers ($ and # are legal).
IDENT        : [A-Za-z] [A-Za-z0-9_$#]* ;
QUOTED_IDENT : '"' ( '""' | ~["] )+ '"' ;
NUMBER       : [0-9]+ ('.' [0-9]+)? ;
STRING       : '\'' ( '\'\'' | ~['] )* '\'' ;

// Punctuation we explicitly recognize; everything else falls into PUNCT.
PUNCT        : [+\-*/<>=,;:!?@&|%^~] ;

// Comments and whitespace get tossed at the lexer level.
LINE_COMMENT : '--' ~[\r\n]* -> skip ;
BLOCK_COMMENT: '/*' .*? '*/' -> skip ;
WS           : [ \t\r\n]+    -> skip ;

// Case-insensitive letter fragments (ANTLR4 lacks built-in CI lexing).
fragment A : [aA]; fragment B : [bB]; fragment C : [cC]; fragment D : [dD];
fragment E : [eE]; fragment F : [fF]; fragment G : [gG]; fragment H : [hH];
fragment I : [iI]; fragment J : [jJ]; fragment K : [kK]; fragment L : [lL];
fragment M : [mM]; fragment N : [nN]; fragment O : [oO]; fragment P : [pP];
fragment Q : [qQ]; fragment R : [rR]; fragment S : [sS]; fragment T : [tT];
fragment U : [uU]; fragment V : [vV]; fragment W : [wW]; fragment X : [xX];
fragment Y : [yY]; fragment Z : [zZ];
