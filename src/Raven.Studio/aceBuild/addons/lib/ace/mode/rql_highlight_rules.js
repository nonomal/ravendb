define(function(require, exports, module) {
    "use strict";

    var oop = require("../lib/oop");
    var TextHighlightRules = require("./text_highlight_rules").TextHighlightRules;
    var JavaScriptHighlightRules = require("./javascript_highlight_rules").JavaScriptHighlightRules;

    var RqlHighlightRules = function() {

        var keywordRegex = /[a-zA-Z_$@\u00a1-\uffff][a-zA-Z0-9_$@\u00a1-\uffff]*\b/;

        var escapedRe = "\\\\(?:x[0-9a-fA-F]{2}|" + // hex
            "u[0-9a-fA-F]{4}|" + // unicode
            "u{[0-9a-fA-F]{1,6}}|" + // es6 unicode
            "[0-2][0-7]{0,2}|" + // oct
            "3[0-7][0-7]?|" + // oct
            "[4-7][0-7]?|" + //oct
            ".)";

        var clausesKeywords = (
            "declare|from|group|where|order|load|select|include|update|match|with|limit|filter|filter_limit|offset"
        );
        this.clausesKeywords = clausesKeywords.split("|");

        var clauseAppendKeywords = (
            "function|index|by"
        );
        this.clauseAppendKeywords = clauseAppendKeywords.split("|");

        var functions = (
            "count|sum|id|key"
        );

        var whereOperators = (
            "all|in|between"
        );

        var whereFunctions = (
            "search|boost|startsWith|endsWith|lucene|exact|within|exists|contains|disjoint|intersects"
        );
        this.whereFunctions = whereFunctions.split("|");

        var withinFunctions = (
            "circle"
        );
        this.withinFunctions = withinFunctions.split("|");

        var orderByFunctions = (
            "random|score"
        );

        var orderByOptions = (
            "desc|asc|descending|ascending"
        );
        var orderByAsOptions = (
            "string|long|double|alphaNumeric"
        );

        var constants = (
            "null"
        );
        var constantsBoolean = (
            "true|false"
        );
        var binaryOperations = (
            "and|or"
        );
        this.binaryOperations = binaryOperations.split("|");

        var operations = (
            ">=|<=|<|>|=|==|!="
        );

        var keywordMapper = this.createKeywordMapper({
            "keyword.clause": clausesKeywords,
            "keyword.clause.clauseAppend": clauseAppendKeywords,
            "keyword.asKeyword": "as",
            "keyword.notKeyword": "not",
            "keyword.orderByOptions": orderByOptions,
            "keyword.orderByAsOptions": orderByAsOptions,
            "keyword.whereOperators": whereOperators,
            "function": functions,
            "function.where.within": withinFunctions,
            "function.orderBy": orderByFunctions,
            "constant.language": constants,
            "constant.language.boolean": constantsBoolean,
            "operations.type.binary": binaryOperations,
            "operations.type": operations
        }, "identifier", true);

        var curelyBracesCount = 0;
        
        // we hold here last token like: with, update, select 
        // so after we reach '{' we know if context should be
        // changed to js (in case of 'update', 'select'
        // or to RQL (in case of 'with')
        var lastPreBracketTokenSeen = null; 
        
        var preBracketTokens = ["select", "update", "with"];

        var commonRules = [ {
            token : "comment",
            regex : "//.*$"
        },  {
            token : "comment",
            start : "/\\*",
            end : "\\*/"
        }, {
            token : "string",           // " string
            regex : '"(?=.)',
            next  : "qqstring"
        }, {
            token : "string",           // ' string
            regex : "'(?=.)",
            next  : "qstring"
        }, {
            token : "string",           // ` string (apache drill)
            regex : "`[^`]*`?"
        }, {
            token : "constant.numeric", // float
            regex : "[+-]?\\d+(?:(?:\\.\\d*)?(?:[eE][+-]?\\d+)?)?\\b"
        }, {
            token : "paren.lparen",
            regex : /{/,
            next: function (currentState) {
                curelyBracesCount++;
                
                return lastPreBracketTokenSeen === "with" ? currentState : "js-start";
            }
        }, {
            token : "paren.lparen",
            regex : /[\[({]/
        }, {
            token : "comma",
            regex : /,/
        }, {
            token : "space",
            regex : /\s+/
        } ];

        var startRule = [ {
            token :  "field",
            regex : /[a-zA-Z_$@\u00a1-\uffff][a-zA-Z0-9_$@\u00a1-\uffff]*(?:\[\])?\.[a-zA-Z0-9_$@\u00a1-\uffff.]*/
        }, {
            token :  "function.where",
            regex : whereFunctions,
            next: "whereFunction"
        }, {
            token : function (token, state, stack) {
                if (preBracketTokens.indexOf(token) !== -1) {
                    lastPreBracketTokenSeen = token;
                }
                
                return keywordMapper(token);
            },
            regex : keywordRegex
        }, {
            token : "operator.where",
            regex : /(?:==|!=|>=|<=|=|<>|>|<)(?=\s)/
        }, {
            token : "paren.rparen",
            regex : /[\])}]/
        } ];

        var whereFunctionsRules = [ {
            token : "identifier",
            regex : keywordRegex
        }, {
            token : "paren.rparen",
            regex : /[)]/,
            next: "start"
        } ];

        this.$rules = {
            "start" : commonRules.concat(startRule),
            "qqstring" : [
                {
                    token : "constant.language.escape",
                    regex : escapedRe
                }, {
                    token : "string",
                    regex : "\\\\$",
                    consumeLineEnd  : true
                }, {
                    token : "string",
                    regex : '"|$',
                    next  : "start"
                }, {
                    defaultToken: "string"
                }
            ],
            "qstring" : [
                {
                    token: "constant.language.escape",
                    regex: escapedRe
                }, {
                    token: "string",
                    regex: "\\\\$",
                    consumeLineEnd: true
                }, {
                    token: "string",
                    regex: "'|$",
                    next: "start"
                }, {
                    defaultToken: "string"
                }
            ],
            "whereFunction" : commonRules.concat(whereFunctionsRules).map(function (rule) {
                return {
                    token: rule.token + ".whereFunction",
                    regex: rule.regex,
                    start: rule.start,
                    end: rule.end,
                    next: rule.next
                };
            })
        };

        this.embedRules(JavaScriptHighlightRules, "js-", [ {
            token : function (value, currentState, stack) {
                if (currentState === "js-string.quasi.start") {
                    return "string.quasi.start";
                }
                if (currentState === "js-qqstring" || currentState === "js-qstring") {
                    return "string";
                }
                curelyBracesCount++;
                return "paren.lparen";
            },
            regex: /{/
        }, {
            token : function (value, currentState, stack) {
                if (currentState !== "js-start" && currentState !== "js-no_regex") {
                    return "string";
                }
                return "paren.rparen";
            },
            regex : /}/,
            next : function (currentState, stack) {
                if (currentState !== "js-start" && currentState !== "js-no_regex") {
                    return currentState;
                }
                if (--curelyBracesCount > 0) {
                    return currentState;
                }
                return "start";
            }
        }]);

        this.normalizeRules();
    };

    oop.inherits(RqlHighlightRules, TextHighlightRules);

    exports.RqlHighlightRules = RqlHighlightRules;
});
