// <copyright file="MapModelExceptionsAttribute.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Runtime
{
    using System;
    using System.Data;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.AspNetCore.Mvc.Filters;

    /// <summary>
    /// Turns the model's exceptions into HTTP answers: a constraint violation (a duplicate key or unique value, a missing
    /// parent, a delete blocked by children) becomes 422, a stale version becomes 412, and a malformed batch becomes 400.
    /// Generated controllers carry it.  Each of these leaves the data unchanged: the failed transaction has rolled back.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class MapModelExceptionsAttribute : ExceptionFilterAttribute
    {
        /// <inheritdoc/>
        public override void OnException(ExceptionContext context)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var (status, title) = context.Exception switch
            {
                DBConcurrencyException => (StatusCodes.Status412PreconditionFailed, "The row has changed since it was read."),
                ConstraintException => (StatusCodes.Status422UnprocessableEntity, "The change violates the model's constraints."),
                InvalidBatchException => (StatusCodes.Status400BadRequest, "The batch is malformed."),
                _ => (0, string.Empty),
            };

            if (status == 0)
            {
                return;
            }

            context.Result = new ObjectResult(new ProblemDetails { Status = status, Title = title, Detail = context.Exception.Message }) { StatusCode = status };
            context.ExceptionHandled = true;
        }
    }
}
