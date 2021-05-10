# L'étude des tâches en couleurs vives.
📁📝⋘?⋙🎨🖌️

_Sorry, cound not resist the pun. Also, my French is horrific. You'll regret it if you try speaking French to me._

And this is a study of .NET `Task`s, not of color blotches.

---

WIP. I have no time to complete the writing right now, and this all will
make little sense to you as is,, without an explanation.

The async/await pattern is notoriously invasive. It wants to propagate
all the way up to main. What do you do to interface a highly-parallel (hundreds
to thousands streams), `Task`-based code to your existing synchronous codebase?

The project is an example that uses [System.Reactive](
https://github.com/dotnet/reactive) to tame the beast in a very natural way.

A full write-up in the works, as is reactive handling of exception with
an exponential backoff and retry when the server signals an overload.

---

Code license: [Apache 2.0](http://www.apache.org/licenses/LICENSE-2.0)  
Documentation: [CC BY-SA 4.0](http://creativecommons.org/licenses/by-sa/4.0/)
