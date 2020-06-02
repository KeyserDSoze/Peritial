using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Expertiser
{
    public class DefaultIndex<T>
        where T : class
    {
        public T Entity { get; }
        public List<string> DeleterRoles { get; }
        public List<string> EditorRoles { get; }
        public DefaultIndex(T entity)
        {
            this.Entity = entity;
        }
        public List<Property> Properties { get; } = new List<Property>();
        public DefaultIndexProperty<T, TProperty> Include<TProperty>(Expression<Func<T, TProperty>> navigationPropertyPath)
        {
            dynamic body = navigationPropertyPath.Body;
            this.Properties.Add(new Property(body.Member));
            this.Last = this.Properties.Last();
            return new DefaultIndexProperty<T, TProperty>(this);
        }
        internal Property Last { get; set; }
    }
    public class Property
    {
        public PropertyInfo PropertyInfo { get; }
        public List<Property> Properties { get; }
        public Property(PropertyInfo propertyInfo)
        {
            this.PropertyInfo = propertyInfo;
            this.Properties = new List<Property>();
        }
    }
    public class DefaultIndexProperty<T, TPreviousProperty>
        where T : class
    {
        public DefaultIndex<T> Entity { get; }
        public DefaultIndexProperty(DefaultIndex<T> entity)
        {
            this.Entity = entity;
        }
        public DefaultIndexProperty<T, TProperty> ThenInclude<TProperty>(Expression<Func<TPreviousProperty, TProperty>> navigationPropertyPath)
        {
            dynamic body = navigationPropertyPath.Body;
            this.Entity.Last.Properties.Add(new Property(body.Member));
            this.Entity.Last = this.Entity.Last.Properties.Last();
            return new DefaultIndexProperty<T, TProperty>(this.Entity);
        }
        public DefaultIndexProperty<T, TProperty> Include<TProperty>(Expression<Func<T, TProperty>> navigationPropertyPath) 
            => this.Entity.Include(navigationPropertyPath);
    }
}
